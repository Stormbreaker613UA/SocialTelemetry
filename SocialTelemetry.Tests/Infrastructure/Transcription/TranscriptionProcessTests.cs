using SocialTelemetry.Application.Contracts.Transcription;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Options;
using SocialTelemetry.Infrastructure.Transcription;

namespace SocialTelemetry.Tests.Infrastructure.Transcription;

public sealed class TranscriptionProcessTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux process-group escape boundary.")]
    public async Task Deliberately_escaped_writer_is_not_killed_and_drain_cleanup_fails_with_a_bounded_safe_error()
    {
        var directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var sentinel = NativeProcessFixture.Start("sentinel", directory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var runner = new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions { TimeoutSeconds = 1, FileCheckMilliseconds = 50 }), NativeProcessFixture.Host);
        var command = new TranscriptionCommand(NativeProcessFixture.Dotnet(), directory,
            NativeProcessFixture.Arguments("root", directory).Append("escape").ToArray(), Path.Combine(directory, "output.bin"), 1024);
        var operation = runner.RunAsync(command, cancellation.Token);
        SafeFileHandle? escaped = null;
        try
        {
            await WaitAsync(() => File.Exists(Path.Combine(directory, "ready")));
            var identities = new[] { "root", "child", "grandchild" }
                .Select(role => File.ReadAllText(Path.Combine(directory, role + ".pid"))).ToArray();
            var pid = int.Parse(identities[2].Split('|')[0]);
            // Test teardown pins the exact intentionally escaping synthetic process, never a reused PID.
            var descriptor = OpenProcessDescriptor(434, pid, 0); // pidfd_open, Linux x64/arm64.
            Assert.True(descriptor >= 0);
            escaped = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            Assert.Equal(identities[2], NativeProcessFixture.Identity(pid));
            await SignalActionAsync(directory, "exit");
            var duration = Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<SpeechToTextException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
            Assert.Equal(SpeechToTextFailure.CleanupFailed, failure.Failure);
            Assert.DoesNotContain(directory, failure.ToString());
            Assert.True(duration.Elapsed < TimeSpan.FromSeconds(5));
            Assert.False(IsRunning(identities[0]));
            Assert.False(IsRunning(identities[1]));
            Assert.True(IsRunning(identities[2]));
            Assert.False(sentinel.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(10), Cancellation); } catch { }
            if (escaped is not null)
            {
                Assert.Equal(0, SignalProcessDescriptor(424, (int)escaped.DangerousGetHandle(), 9, 0, 0)); // pidfd_send_signal.
                escaped.Dispose();
            }
            if (!sentinel.HasExited) sentinel.Kill();
            await sentinel.WaitForExitAsync(Cancellation);
            DeleteTemporaryDirectory(directory);
        }
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long OpenProcessDescriptor(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern int SignalProcessDescriptor(long number, int descriptor, int signal, nint information, uint flags);

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("output")]
    [InlineData("file")]
    [InlineData("exit")]
    [InlineData("failure")]
    public async Task Owned_child_and_grandchild_terminate_even_after_root_exit_and_unrelated_sentinel_survives(string trigger)
    {
        var directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        using var sentinel = NativeProcessFixture.Start("sentinel", directory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var settings = new WhisperCppOptions { TimeoutSeconds = 3, FileCheckMilliseconds = 50,
            ProcessOutputCharacters = trigger == "output" ? 1024 : 65536 };
        var runner = new TranscriptionProcessRunner(Options.Create(settings), NativeProcessFixture.Host);
        var command = new TranscriptionCommand(NativeProcessFixture.Dotnet(), directory,
            NativeProcessFixture.Arguments("root", directory), Path.Combine(directory, "output.bin"), 1024);
        var operation = runner.RunAsync(command, cancellation.Token);
        try
        {
            await WaitAsync(() => File.Exists(Path.Combine(directory, "ready")));
            var identities = new[] { "root", "child", "grandchild" }
                .Select(role => File.ReadAllText(Path.Combine(directory, role + ".pid"))).ToArray();
            Assert.All(identities, identity => Assert.True(IsRunning(identity)));
            // The exited-root cases leave children holding both redirected writers.
            if (trigger == "cancel") cancellation.Cancel();
            else if (trigger == "timeout") cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            else await SignalActionAsync(directory, trigger);

            if (trigger is "cancel" or "timeout")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
            else if (trigger is "file" or "output")
            {
                var failure = await Assert.ThrowsAsync<SpeechToTextException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
                Assert.Equal(SpeechToTextFailure.LimitExceeded, failure.Failure);
                Assert.DoesNotContain(directory, failure.ToString());
            }
            else Assert.Equal(trigger == "failure" ? 7 : 0, await operation.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
            // Check the return boundary: no grace period for a surviving owned descendant.
            Assert.All(identities, identity => Assert.False(IsRunning(identity)));
            Assert.False(sentinel.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(10), Cancellation); } catch { }
            if (!sentinel.HasExited) sentinel.Kill();
            await sentinel.WaitForExitAsync(Cancellation);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Normal_exit_preserves_arguments_working_directory_and_nonzero_status_without_a_shell()
    {
        var directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        string[] arguments = ["", "two words", "quote\"value", @"trailing slash\", "$not-a-shell", "кириллица"];
        try
        {
            var runner = new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions { TimeoutSeconds = 3, FileCheckMilliseconds = 50 }), NativeProcessFixture.Host);
            var command = new TranscriptionCommand(NativeProcessFixture.Dotnet(), directory,
                NativeProcessFixture.Arguments("arguments", directory).Concat(arguments).ToArray(), Path.Combine(directory, "unused.bin"), 1024);
            Assert.Equal(7, await runner.RunAsync(command, Cancellation).WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
            Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(directory, "arguments.json"), Cancellation)));
            Assert.Equal(Path.GetFullPath(directory), await File.ReadAllTextAsync(Path.Combine(directory, "working-directory.txt"), Cancellation));
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task Startup_failure_and_precancelled_launch_are_safe_and_do_not_start_a_fixture()
    {
        var directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            var runner = new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions()), NativeProcessFixture.Host);
            var command = new TranscriptionCommand(Path.Combine(directory, "missing.exe"), directory, [], Path.Combine(directory, "unused.bin"), 1024);
            for (var attempt = 0; attempt < 3; attempt++)
                Assert.Equal(SpeechToTextFailure.ConfigurationRequired,
                    (await Assert.ThrowsAsync<SpeechToTextException>(() => runner.RunAsync(command, Cancellation))).Failure);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(command with
                { Executable = NativeProcessFixture.Dotnet(), Arguments = NativeProcessFixture.Arguments("root", directory) }, cancelled.Token));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    private static bool IsRunning(string identity)
    {
        var pid = int.Parse(identity.Split('|')[0]);
        try
        {
            if (NativeProcessFixture.Identity(pid) != identity) return false; // Reuse is not this fixture.
            if (OperatingSystem.IsLinux())
            {
                var status = File.ReadAllText($"/proc/{pid}/stat");
                // Z/X have terminated; reaping by the descendant's parent/init can happen later.
                return status[(status.LastIndexOf(')') + 2)..].Split(' ')[0] is not ("Z" or "X");
            }
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        { return false; }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }

    private static async Task SignalActionAsync(string directory, string action)
    {
        var path = Path.Combine(directory, "action");
        await File.WriteAllTextAsync(path + ".new", action, Cancellation);
        File.Move(path + ".new", path);
    }

    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", "native " + Guid.NewGuid().ToString("N"));
    private static void DeleteTemporaryDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests")) + Path.DirectorySeparatorChar, fullPath);
        Directory.Delete(fullPath, recursive: true);
    }
}
