using SocialTelemetry.Application.Contracts.Transcription;
using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SocialTelemetry.Infrastructure.Transcription;

namespace SocialTelemetry.Infrastructure.Platform.Linux;

[SupportedOSPlatform("linux")]
internal sealed class LinuxTranscriptionProcess(int leaderPid, int output, int errors, int control) : OwnedTranscriptionProcess
{
    private int? rootExitCode;
    private bool reaped;
    private readonly object lifecycle = new();
    private readonly byte[] statusBytes = new byte[4];
    private int statusLength;
    private bool terminated;

    public new static LinuxTranscriptionProcess Start(TranscriptionCommand command, NativeTranscriptionHost host)
    {
        // Linux x64/arm64 glibc/musl spawn ABIs: opaque attr 336 bytes, file-actions 80 bytes.
        // Access them only through libc functions; no managed fork callbacks in a multithreaded host.
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new Win32Exception();
        var attributes = Marshal.AllocHGlobal(336);
        var actions = Marshal.AllocHGlobal(80);
        var attributesInitialized = false;
        var actionsInitialized = false;
        int[] outputPipe = [-1, -1];
        int[] errorPipe = [-1, -1];
        int[] controlPipe = [-1, -1];
        try
        {
            CheckResult(Native.posix_spawnattr_init(attributes));
            attributesInitialized = true;
            CheckResult(Native.posix_spawn_file_actions_init(actions));
            actionsInitialized = true;
            CheckResult(Native.posix_spawnattr_setflags(attributes, 0x80)); // SETSID, before exec.
            CheckSystem(Native.pipe2(outputPipe, 0x80000)); // O_CLOEXEC, atomic against other spawns.
            CheckSystem(Native.pipe2(errorPipe, 0x80000));
            CheckSystem(Native.pipe2(controlPipe, 0x80000));
            CheckSystem(Native.fcntl(outputPipe[0], 4, 0x800)); // F_SETFL, O_NONBLOCK on readers only.
            CheckSystem(Native.fcntl(errorPipe[0], 4, 0x800));
            CheckSystem(Native.fcntl(controlPipe[0], 4, 0x800));
            CheckResult(Native.posix_spawn_file_actions_addchdir_np(actions, command.WorkingDirectory));
            CheckResult(Native.posix_spawn_file_actions_addopen(actions, 0, "/dev/null", 0, 0));
            CheckResult(Native.posix_spawn_file_actions_adddup2(actions, outputPipe[1], 1));
            CheckResult(Native.posix_spawn_file_actions_adddup2(actions, errorPipe[1], 2));
            CheckResult(Native.posix_spawn_file_actions_adddup2(actions, controlPipe[1], 3));
            var launcher = host.LauncherCommand(command);
            using var arguments = new NativeStrings(new[] { launcher.Executable }.Concat(launcher.Arguments));
            using var environment = new NativeStrings(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .Select(entry => $"{entry.Key}={entry.Value}"));
            CheckResult(Native.posix_spawn(out var pid, launcher.Executable, actions, attributes, arguments.Pointer, environment.Pointer));
            var process = new LinuxTranscriptionProcess(pid, outputPipe[0], errorPipe[0], controlPipe[0]);
            outputPipe[0] = errorPipe[0] = controlPipe[0] = -1; // Transfer only our reader descriptors.
            return process;
        }
        finally
        {
            foreach (var descriptor in outputPipe.Concat(errorPipe).Concat(controlPipe))
                if (descriptor >= 0) Native.close(descriptor);
            if (actionsInitialized) Native.posix_spawn_file_actions_destroy(actions);
            if (attributesInitialized) Native.posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
        }
    }

    public override bool TryGetExitCode(out int exitCode)
    {
        lock (lifecycle) return ReadExitCode(out exitCode);
    }

    private bool ReadExitCode(out int exitCode)
    {
        if (rootExitCode is { } cached) { exitCode = cached; return true; }
        if (terminated) { exitCode = 0; return true; }
        var bytes = new byte[4 - statusLength];
        var count = Native.read(control, bytes, (nuint)bytes.Length);
        if (count < 0)
        {
            if (Marshal.GetLastPInvokeError() is not (4 or 11)) throw new Win32Exception();
        }
        else
        {
            Array.Copy(bytes, 0, statusBytes, statusLength, (int)count);
            statusLength += (int)count;
            if (statusLength == 4)
            {
                exitCode = BitConverter.ToInt32(statusBytes);
                if (exitCode < 0) throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
                rootExitCode = exitCode;
                return true;
            }
            if (count == 0) throw new Win32Exception(); // Launcher failed; never treat EOF as success.
        }
        exitCode = 0;
        return false;
    }

    public override async Task<int> ReadAsync(bool standardError, byte[] buffer, int pollMilliseconds, CancellationToken cancellationToken)
    {
        var descriptor = standardError ? errors : output;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Native.read(descriptor, buffer, (nuint)buffer.Length);
            if (count >= 0) return checked((int)count);
            var error = Marshal.GetLastPInvokeError();
            if (error == 4) continue;
            if (error != 11) throw new Win32Exception(); // EAGAIN, not a blocking pipe read.
            await Task.Delay(pollMilliseconds, cancellationToken);
        }
    }

    public override async Task TerminateAsync(int pollMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            // The launcher stays alive after reporting the STT root exit. It pins the group even
            // when the backend is PID 1 and .NET reaps all exited children. Signal the group once,
            // before that live anchor can be reaped; never send a later signal to a recyclable PGID.
            ConfirmLeaderOwnership();
            CheckSystem(Native.kill(-leaderPid, 9));
            lock (lifecycle) terminated = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!HasRunningGroupMember(cancellationToken))
                {
                    int result;
                    do { result = Native.waitpid(leaderPid, out _, 1); }
                    while (result < 0 && Marshal.GetLastPInvokeError() == 4);
                    if (result == 0) { await Task.Delay(pollMilliseconds, cancellationToken); continue; }
                    // A PID-1 .NET host may already have reaped this terminated launcher.
                    if (result != leaderPid && !(result < 0 && Marshal.GetLastPInvokeError() == 10)) throw new Win32Exception();
                    reaped = true;
                    return;
                }
                await Task.Delay(pollMilliseconds, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new SpeechToTextException(SpeechToTextFailure.CleanupFailed); }
    }

    private void ConfirmLeaderOwnership()
    {
        var information = new SignalInformation();
        while (Native.waitid(1, leaderPid, ref information, 1 | 4 | 0x01000000) != 0)
        {
            if (Marshal.GetLastPInvokeError() != 4) throw new Win32Exception();
        }
    }

    private bool HasRunningGroupMember(CancellationToken cancellationToken)
    {
        // Verify group quiescence, not just root exit. Never use scanned PIDs as kill targets.
        // /proc must be available/readable. Defunct descendants have terminated; their parent/init reaps them.
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!int.TryParse(Path.GetFileName(directory), out _)) continue;
            string status;
            try { status = File.ReadAllText(Path.Combine(directory, "stat")); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            var fields = status[(status.LastIndexOf(')') + 2)..].Split(' ');
            if (fields.Length < 4 || !int.TryParse(fields[2], out var group)) throw new Win32Exception();
            if (group == leaderPid && fields[0] is not ("Z" or "X")) return true;
        }
        return false;
    }

    public override void Dispose()
    {
        Native.close(output);
        Native.close(errors);
        Native.close(control);
        // Last best effort on a failed cleanup. No group signals after reaping/releasing the PID anchor.
        if (!reaped && !terminated)
        {
            var information = new SignalInformation();
            if (Native.waitid(1, leaderPid, ref information, 1 | 4 | 0x01000000) == 0)
            {
                Native.kill(-leaderPid, 9);
                if (information.Pid == leaderPid) Native.waitpid(leaderPid, out _, 1);
            }
        }
    }

    internal static void RunLauncher(string executable, string[] arguments)
    {
        // No ASP.NET startup, storage, database, credentials or inference. This private child
        // only anchors its already-created session and reports the direct STT root's status.
        var actions = Marshal.AllocHGlobal(80);
        var initialized = false;
        var exitCode = -1;
        try
        {
            CheckResult(Native.posix_spawn_file_actions_init(actions));
            initialized = true;
            CheckResult(Native.posix_spawn_file_actions_addclose(actions, 3));
            using var nativeArguments = new NativeStrings(new[] { executable }.Concat(arguments));
            using var environment = new NativeStrings(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .Select(entry => $"{entry.Key}={entry.Value}"));
            CheckResult(Native.posix_spawn(out var pid, executable, actions, 0, nativeArguments.Pointer, environment.Pointer));
            int status;
            int waited;
            do { waited = Native.waitpid(pid, out status, 0); }
            while (waited < 0 && Marshal.GetLastPInvokeError() == 4);
            if (waited != pid) throw new Win32Exception();
            exitCode = (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F);
        }
        catch (Exception exception) when (exception is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        { /* Only a fixed technical status crosses the control pipe, never native diagnostics. */ }
        finally
        {
            if (initialized) Native.posix_spawn_file_actions_destroy(actions);
            Marshal.FreeHGlobal(actions);
        }
        var message = BitConverter.GetBytes(exitCode);
        Native.write(3, message, 4);
        Native.close(3);
        // Remain a live group anchor until the owner terminates the group, including on root failure.
        while (true) Thread.Sleep(Timeout.Infinite);
    }

    private static void CheckResult(int result)
    {
        if (result != 0) throw new Win32Exception(result);
    }
    private static void CheckSystem(int result)
    {
        if (result < 0) throw new Win32Exception();
    }

    private sealed class NativeStrings : IDisposable
    {
        public nint Pointer { get; }
        private readonly List<nint> strings = [];
        public NativeStrings(IEnumerable<string> values)
        {
            try
            {
                foreach (var value in values)
                {
                    if (value.Contains('\0')) throw new Win32Exception();
                    strings.Add(Marshal.StringToCoTaskMemUTF8(value));
                }
                Pointer = Marshal.AllocHGlobal((strings.Count + 1) * nint.Size);
                for (var index = 0; index < strings.Count; index++) Marshal.WriteIntPtr(Pointer, index * nint.Size, strings[index]);
                Marshal.WriteIntPtr(Pointer, strings.Count * nint.Size, 0);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var value in strings) Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct SignalInformation
    {
        [FieldOffset(8)] public int Code;
        [FieldOffset(16)] public int Pid;
        [FieldOffset(24)] public int Status;
    }
    private static class Native
    {
        [DllImport("libc")] public static extern int posix_spawnattr_init(nint attributes);
        [DllImport("libc")] public static extern int posix_spawnattr_destroy(nint attributes);
        [DllImport("libc")] public static extern int posix_spawnattr_setflags(nint attributes, short flags);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_init(nint actions);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_destroy(nint actions);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addchdir_np(nint actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string directory);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addopen(nint actions, int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_adddup2(nint actions, int descriptor, int target);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addclose(nint actions, int descriptor);
        [DllImport("libc")] public static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string executable, nint actions, nint attributes, nint arguments, nint environment);
        [DllImport("libc", SetLastError = true)] public static extern int pipe2([Out] int[] descriptors, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int fcntl(int descriptor, int operation, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int close(int descriptor);
        [DllImport("libc", SetLastError = true)] public static extern nint read(int descriptor, [Out] byte[] buffer, nuint count);
        [DllImport("libc", SetLastError = true)] public static extern nint write(int descriptor, byte[] buffer, nuint count);
        [DllImport("libc", SetLastError = true)] public static extern int waitid(int kind, int pid, ref SignalInformation information, int options);
        [DllImport("libc", SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);
        [DllImport("libc", SetLastError = true)] public static extern int kill(int pid, int signal);
    }
}
