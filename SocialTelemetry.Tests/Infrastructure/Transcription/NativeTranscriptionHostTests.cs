using System.Diagnostics;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Infrastructure.Transcription;

namespace SocialTelemetry.Tests.Infrastructure.Transcription;

public sealed class NativeTranscriptionHostTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public void Descriptor_is_explicit_immutable_and_rejects_class_libraries_and_missing_hosts()
    {
        var descriptor = NativeProcessFixture.Host;
        Assert.Equal([typeof(Program).Assembly.Location], descriptor.PrefixArguments);
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(descriptor.Executable));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)descriptor.PrefixArguments).Clear());
        Assert.Equal(SpeechToTextFailure.ConfigurationRequired,
            Assert.Throws<SpeechToTextException>(() => NativeTranscriptionHost.FromHostAssembly(typeof(TranscriptionProcessRunner).Assembly)).Failure);
        Assert.Equal(SpeechToTextFailure.ConfigurationRequired,
            Assert.Throws<SpeechToTextException>(() => NativeTranscriptionHost.ForDotnet(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), typeof(Program).Assembly)).Failure);
        Assert.Equal(SpeechToTextFailure.ConfigurationRequired,
            Assert.Throws<SpeechToTextException>(() => NativeTranscriptionHost.ForAppHost(typeof(TranscriptionProcessRunner).Assembly.Location, typeof(Program).Assembly)).Failure);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux requires an explicitly bound executable host.")]
    public async Task Missing_host_fails_closed_without_starting_a_native_process()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var command = new TranscriptionCommand(NativeProcessFixture.Dotnet(), directory,
                NativeProcessFixture.Arguments("arguments", directory), Path.Combine(directory, "unused"), 1024);
            var runner = new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions()));
            var failure = await Assert.ThrowsAsync<SpeechToTextException>(() => runner.RunAsync(command, TestContext.Current.CancellationToken));
            Assert.Equal(SpeechToTextFailure.ConfigurationRequired, failure.Failure);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task Real_dotnet_Api_bootstrap_rejects_incomplete_private_invocation_before_host_startup()
    {
        await AssertPrivateBootstrapAsync(NativeProcessFixture.Host);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-built Api apphost packaging; Linux apphost is not built by this fixture.")]
    public async Task Real_Api_apphost_rejects_private_invocation_before_host_startup()
    {
        var executable = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        Assert.True(File.Exists(executable));
        await AssertPrivateBootstrapAsync(NativeTranscriptionHost.ForAppHost(executable, typeof(Program).Assembly));
    }

    private static async Task AssertPrivateBootstrapAsync(NativeTranscriptionHost descriptor)
    {
        var start = new ProcessStartInfo(descriptor.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        };
        foreach (var argument in descriptor.PrefixArguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(NativeTranscriptionBootstrap.LauncherArgument);
        // If normal application startup runs, this invalid configuration must fail instead.
        start.Environment["Persistence__Provider"] = "must-not-start-normal-host";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic host did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(78, process.ExitCode);
            Assert.Equal("", await output);
            Assert.Equal("", await errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }
}
