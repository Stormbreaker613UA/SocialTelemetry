using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SocialTelemetry.Api.Infrastructure.Transcription;

[SupportedOSPlatform("windows")]
internal sealed class WindowsTranscriptionProcess(SafeFileHandle job, SafeFileHandle root,
    SafeFileHandle output, SafeFileHandle errors) : OwnedTranscriptionProcess
{
    private int? rootExitCode;

    public new static WindowsTranscriptionProcess Start(TranscriptionCommand command)
    {
        var job = Native.CreateJobObjectW(0, null);
        Check(!job.IsInvalid);
        SafeFileHandle? output = null;
        SafeFileHandle? errors = null;
        try
        {
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE; no breakaway.
            Check(Native.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>()));
            output = CreateOutputPipe(out var outputWriter);
            using (outputWriter)
            {
                errors = CreateOutputPipe(out var errorWriter);
                using (errorWriter)
                {
                    var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
                    using var input = Native.CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, 0);
                    Check(!input.IsInvalid);
                    using var attributes = new LaunchAttributes(job, input, outputWriter, errorWriter);
                    var startup = new StartupInfoEx
                    {
                        Info = new StartupInfo
                        {
                            Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                            Input = input.DangerousGetHandle(), Output = outputWriter.DangerousGetHandle(),
                            Error = errorWriter.DangerousGetHandle()
                        },
                        Attributes = attributes.Pointer
                    };
                    // JOB_LIST associates containment as part of CreateProcess, before any child code.
                    // HANDLE_LIST prevents concurrent launches from inheriting another operation's pipes.
                    Check(Native.CreateProcessW(command.Executable, CommandLine(command), 0, 0, true,
                        0x00080000 | 0x08000000, 0, command.WorkingDirectory, ref startup, out var created));
                    using var thread = new SafeFileHandle(created.Thread, ownsHandle: true);
                    return new WindowsTranscriptionProcess(job, new SafeFileHandle(created.Process, ownsHandle: true), output, errors);
                }
            }
        }
        catch
        {
            job.Dispose();
            output?.Dispose();
            errors?.Dispose();
            throw;
        }
    }

    public override bool TryGetExitCode(out int exitCode)
    {
        if (rootExitCode is { } cached) { exitCode = cached; return true; }
        var wait = Native.WaitForSingleObject(root, 0);
        if (wait == 0x102) { exitCode = 0; return false; }
        Check(wait == 0);
        Check(Native.GetExitCodeProcess(root, out var result));
        rootExitCode = exitCode = unchecked((int)result);
        return true;
    }

    public override async Task<int> ReadAsync(bool standardError, byte[] buffer, int pollMilliseconds, CancellationToken cancellationToken)
    {
        var pipe = standardError ? errors : output;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Native.PeekNamedPipe(pipe, 0, 0, 0, out var available, 0))
            {
                if (Marshal.GetLastPInvokeError() == 109) return 0; // All writers closed.
                throw new Win32Exception();
            }
            if (available > 0)
            {
                Check(Native.ReadFile(pipe, buffer, (uint)Math.Min(available, buffer.Length), out var read, 0));
                return (int)read;
            }
            // Anonymous ReadFile is synchronous. Peek first so an inherited writer cannot block it.
            await Task.Delay(pollMilliseconds, cancellationToken);
        }
    }

    public override async Task TerminateAsync(int pollMilliseconds, CancellationToken cancellationToken)
    {
        Check(Native.TerminateJobObject(job, 1));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(Native.QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), 0));
            if (accounting.ActiveProcesses == 0 && TryGetExitCode(out _)) return;
            await Task.Delay(pollMilliseconds, cancellationToken);
        }
    }

    public override void Dispose()
    {
        job.Dispose(); // Also terminates on unexpected caller failure; never look up a process by PID.
        root.Dispose();
        output.Dispose();
        errors.Dispose();
    }

    private static SafeFileHandle CreateOutputPipe(out SafeFileHandle writer)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        Check(Native.CreatePipe(out var reader, out writer, ref security, 0));
        if (Native.SetHandleInformation(reader, 1, 0)) return reader;
        reader.Dispose();
        writer.Dispose();
        throw new Win32Exception();
    }

    private static StringBuilder CommandLine(TranscriptionCommand command)
    {
        // Win32 uses a command line, not an argv array. Match .NET's CRT quoting, without a shell.
        var result = new StringBuilder();
        foreach (var argument in new[] { command.Executable }.Concat(command.Arguments))
        {
            if (argument.Contains('\0')) throw new Win32Exception();
            if (result.Length > 0) result.Append(' ');
            result.Append('"');
            var backslashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { backslashes++; continue; }
                result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                result.Append(character);
                backslashes = 0;
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
        }
        return result;
    }

    private static void Check(bool succeeded)
    {
        if (!succeeded) throw new Win32Exception();
    }

    private sealed class LaunchAttributes : IDisposable
    {
        public nint Pointer { get; private set; }
        private nint handles;
        private nint jobs;
        private bool initialized;

        public LaunchAttributes(SafeFileHandle job, SafeFileHandle input, SafeFileHandle output, SafeFileHandle errors)
        {
            try
            {
                nuint size = 0;
                Native.InitializeProcThreadAttributeList(0, 2, 0, ref size);
                Pointer = Marshal.AllocHGlobal(checked((int)size));
                Check(Native.InitializeProcThreadAttributeList(Pointer, 2, 0, ref size));
                initialized = true;
                handles = Marshal.AllocHGlobal(3 * nint.Size);
                Marshal.WriteIntPtr(handles, input.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, nint.Size, output.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, 2 * nint.Size, errors.DangerousGetHandle());
                jobs = Marshal.AllocHGlobal(nint.Size);
                Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                Check(Native.UpdateProcThreadAttribute(Pointer, 0, 0x20002, handles, (nuint)(3 * nint.Size), 0, 0));
                Check(Native.UpdateProcThreadAttribute(Pointer, 0, 0x2000D, jobs, (nuint)nint.Size, 0, 0));
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (initialized) Native.DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(jobs);
            Pointer = handles = jobs = 0;
            initialized = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XCharacters, YCharacters, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref JobLimits limits, int length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool QueryInformationJobObject(SafeFileHandle job, int information, out JobAccounting accounting, int length, nint returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, ref SecurityAttributes security, uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
        [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processSecurity, nint threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PeekNamedPipe(SafeFileHandle pipe, nint buffer, uint size, nint read, out uint available, nint remaining);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ReadFile(SafeFileHandle file, [Out] byte[] buffer, uint count, out uint read, nint overlapped);
    }
}
