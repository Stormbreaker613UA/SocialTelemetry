using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.Runtime;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Api.Infrastructure.Transcription;

namespace SocialTelemetry.Tests.Infrastructure.Transcription;

public sealed class WhisperCppTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("aac")]
    [InlineData("m4a")]
    [InlineData("ogg")]
    [InlineData("webm")]
    public async Task Supported_containers_are_decoded_verified_and_transcribed_with_automatic_language(string format)
    {
        var settings = Settings();
        var runner = new FakeProcess();
        using var client = Client(settings, runner);
        using var input = new MemoryStream(Header(format));
        var result = await client.TranscribeAsync(input, Cancellation);
        Assert.Equal("Synthetic words", result.Text);
        Assert.Equal("whisper-cpp", result.ProviderId);
        Assert.Equal(settings.ModelId, result.ModelId);
        Assert.Equal("whisper-cli-text-v1", result.TranscriptionVersion);
        Assert.Equal(2, runner.Commands.Count);
        var decoder = runner.Commands[0];
        Assert.Contains("pcm_s16le", decoder.Arguments);
        Assert.Contains("16000", decoder.Arguments);
        Assert.Contains("file", decoder.Arguments);
        Assert.Contains("wav,mp3,mov,aac,ogg,matroska,webm", decoder.Arguments);
        var whisper = runner.Commands[1];
        Assert.Equal("auto", whisper.Arguments[whisper.Arguments.ToList().IndexOf("--language") + 1]);
        Assert.DoesNotContain("--translate", whisper.Arguments);
        Assert.DoesNotContain("--diarize", whisper.Arguments);
        Assert.Contains("--output-txt", whisper.Arguments);
        Assert.DoesNotContain(Path.Combine(root, "media"), whisper.WorkingDirectory);
        AssertClean(settings);
    }

    [Theory]
    [InlineData("unknown", SpeechToTextFailure.InvalidAudio)]
    [InlineData("decoder-exit", SpeechToTextFailure.InvalidAudio)]
    [InlineData("bad-wave", SpeechToTextFailure.InvalidAudio)]
    [InlineData("wrong-rate", SpeechToTextFailure.InvalidAudio)]
    [InlineData("wrong-bits", SpeechToTextFailure.InvalidAudio)]
    [InlineData("duration", SpeechToTextFailure.LimitExceeded)]
    [InlineData("input-size", SpeechToTextFailure.LimitExceeded)]
    [InlineData("whisper-exit", SpeechToTextFailure.TranscriptionFailed)]
    [InlineData("empty-result", SpeechToTextFailure.InvalidResult)]
    [InlineData("missing-result", SpeechToTextFailure.InvalidResult)]
    [InlineData("bad-utf8", SpeechToTextFailure.InvalidResult)]
    [InlineData("result-limit", SpeechToTextFailure.LimitExceeded)]
    public async Task Invalid_decoder_or_whisper_outputs_fail_safely_and_clean_work_files(string fault, SpeechToTextFailure expected)
    {
        var settings = Settings();
        settings.DurationSeconds = 1;
        if (fault == "input-size") settings.InputBytes = 2;
        if (fault == "result-limit") settings.ResultCharacters = 5;
        var runner = new FakeProcess
        {
            Execute = async (command, token) =>
            {
                if (!command.Arguments.Contains("--output-txt"))
                {
                    if (fault == "decoder-exit") return 1;
                    var wave = Wave(fault == "duration" ? 2 : 1);
                    if (fault == "wrong-rate") BitConverter.GetBytes(8000).CopyTo(wave, 24);
                    if (fault == "wrong-bits") wave[34] = 8;
                    await File.WriteAllBytesAsync(command.OutputFile, fault == "bad-wave" ? [1, 2, 3] : wave, token);
                    return 0;
                }
                if (fault == "whisper-exit") return 1;
                if (fault == "missing-result") return 0;
                if (fault == "bad-utf8") await File.WriteAllBytesAsync(command.OutputFile, [255], token);
                else await File.WriteAllTextAsync(command.OutputFile, fault == "empty-result" ? " " : "Synthetic words", token);
                return 0;
            }
        };
        using var client = Client(settings, runner);
        using var input = new MemoryStream(fault == "unknown" ? Encoding.UTF8.GetBytes("not audio") : Header("mp3"));
        var error = await Assert.ThrowsAsync<SpeechToTextException>(() => client.TranscribeAsync(input, Cancellation));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain(root, error.ToString());
        Assert.DoesNotContain("Synthetic words", error.ToString());
        AssertClean(settings);
    }

    [Theory]
    [InlineData("decoder")]
    [InlineData("whisper")]
    [InlineData("model")]
    [InlineData("relative")]
    [InlineData("media-temp")]
    [InlineData("staging-temp")]
    public async Task Missing_or_unsafe_configuration_is_safe_and_does_not_create_work(string dependency)
    {
        var settings = Settings();
        switch (dependency)
        {
            case "decoder": settings.FfmpegExecutable = null; break;
            case "whisper": settings.WhisperExecutable = Path.Combine(root, "absent.exe"); break;
            case "model": settings.ModelFile = Path.Combine(root, "absent.bin"); break;
            case "relative": settings.ModelFile = "relative.bin"; break;
            case "media-temp": settings.TemporaryDirectory = Path.Combine(root, "media"); break;
            case "staging-temp": settings.TemporaryDirectory = Path.Combine(root, "media", ".staging", "work"); break;
        }
        var runner = new FakeProcess();
        using var client = Client(settings, runner);
        using var input = new MemoryStream(Header("mp3"));
        var error = await Assert.ThrowsAsync<SpeechToTextException>(() => client.TranscribeAsync(input, Cancellation));
        Assert.Equal(SpeechToTextFailure.ConfigurationRequired, error.Failure);
        Assert.DoesNotContain(root, error.Message);
        Assert.Empty(runner.Commands);
        Assert.False(Directory.Exists(Path.Combine(root, "work")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_or_cancellation_after_decoding_stops_execution_and_removes_work(bool callerCancellation)
    {
        var settings = Settings();
        settings.TimeoutSeconds = callerCancellation ? 30 : 1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var runner = new FakeProcess { Execute = async (command, token) =>
        {
            if (!command.Arguments.Contains("--output-txt")) { await File.WriteAllBytesAsync(command.OutputFile, Wave(), token); return 0; }
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return 0; }
            finally { stopped = true; }
        } };
        using var client = Client(settings, runner);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        using var input = new MemoryStream(Header("wav"));
        var request = client.TranscribeAsync(input, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        if (callerCancellation)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        else Assert.Equal(SpeechToTextFailure.TimedOut, (await Assert.ThrowsAsync<SpeechToTextException>(() => request)).Failure);
        Assert.True(stopped);
        AssertClean(settings);
    }

    [Fact]
    public async Task Process_concurrency_is_bounded_and_slots_are_released_after_completion()
    {
        var settings = Settings();
        var active = 0;
        var maximum = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeProcess { Execute = async (command, token) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            try
            {
                await Task.Yield();
                entered.TrySetResult();
                await finish.Task.WaitAsync(token);
                if (command.Arguments.Contains("--output-txt")) await File.WriteAllTextAsync(command.OutputFile, "Text", token);
                else await File.WriteAllBytesAsync(command.OutputFile, Wave(), token);
                return 0;
            }
            finally { Interlocked.Decrement(ref active); }
        } };
        using var client = Client(settings, runner);
        using var first = new MemoryStream(Header("mp3"));
        using var second = new MemoryStream(Header("mp3"));
        var initial = client.TranscribeAsync(first, Cancellation);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            var busy = await Assert.ThrowsAsync<SpeechToTextException>(() => client.TranscribeAsync(second, Cancellation));
            Assert.Equal(SpeechToTextFailure.Busy, busy.Failure);
        }
        finally { finish.TrySetResult(); }
        await initial;
        second.Position = 0;
        await client.TranscribeAsync(second, Cancellation);
        Assert.Equal(1, maximum);
        Assert.Equal(4, runner.Commands.Count);
        AssertClean(settings);
    }

    [Fact]
    public async Task Native_runner_discards_output_and_rejects_output_overflow()
    {
        Directory.CreateDirectory(root);
        var executable = FindDotnet();
        var command = new TranscriptionCommand(executable, root, ["--info"], Path.Combine(root, "unused.txt"), 1024);
        Assert.Equal(0, await new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions())).RunAsync(command, Cancellation));
        var limited = new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions { ProcessOutputCharacters = 1 }));
        Assert.Equal(SpeechToTextFailure.LimitExceeded,
            (await Assert.ThrowsAsync<SpeechToTextException>(() => limited.RunAsync(command, Cancellation))).Failure);
    }

    [Fact]
    public async Task Native_runner_missing_executable_does_not_mask_safe_configuration_error()
    {
        Directory.CreateDirectory(root);
        var runner = new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions()));
        var error = await Assert.ThrowsAsync<SpeechToTextException>(() => runner.RunAsync(
            new(Path.Combine(root, "absent.exe"), root, [], Path.Combine(root, "output.txt"), 1024), Cancellation));
        Assert.Equal(SpeechToTextFailure.ConfigurationRequired, error.Failure);
        Assert.DoesNotContain(root, error.ToString());
    }

    [Fact]
    public async Task Native_runner_cancellation_terminates_owned_process_before_returning()
    {
        Directory.CreateDirectory(root);
        var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "ping.exe") : "/bin/sleep";
        string[] arguments = OperatingSystem.IsWindows() ? ["-n", "30", "127.0.0.1"] : ["30"];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));
        var runner = new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new(executable, root, arguments, Path.Combine(root, "unused.txt"), 1024), cancellation.Token));
    }

    private WhisperCppOptions Settings()
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "synthetic.exe");
        var model = Path.Combine(root, "synthetic.bin");
        File.WriteAllBytes(executable, [1]); File.WriteAllBytes(model, [1]);
        return new() { FfmpegExecutable = executable, WhisperExecutable = executable, ModelFile = model,
            TemporaryDirectory = Path.Combine(root, "work"), ModelId = "multilingual-synthetic" };
    }
    private WhisperCppSpeechToTextClient Client(WhisperCppOptions settings, FakeProcess runner) => new(
        Options.Create(settings), runner,
        new ApplicationPaths(Options.Create(new ApplicationDataOptions { RootDirectory = root }), new TestEnvironment { ContentRootPath = root }),
        Options.Create(new AttachmentStorageOptions { LocalDirectory = "media" }), Options.Create(new ProfileStorageOptions()),
        NullLogger<WhisperCppSpeechToTextClient>.Instance);
    private static void AssertClean(WhisperCppOptions settings) => Assert.Empty(Directory.GetDirectories(settings.TemporaryDirectory!));
    private static byte[] Header(string format) => format switch
    {
        "wav" => Wave(), "mp3" => "ID3synthetic"u8.ToArray(), "aac" => [255, 241, 1, 2],
        "m4a" => [0, 0, 0, 12, 102, 116, 121, 112, 77, 52, 65, 32],
        "ogg" => "OggSsynthetic"u8.ToArray(), "webm" => [26, 69, 223, 163, 1, 2], _ => []
    };
    private static byte[] Wave(int seconds = 1)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + seconds * 32000); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(seconds * 32000);
        writer.Write(new byte[seconds * 32000]); writer.Flush(); return stream.ToArray();
    }
    private static string FindDotnet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (host is not null && File.Exists(host)) return host;
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, name)).First(File.Exists);
    }
    private sealed class FakeProcess : ITranscriptionProcessRunner
    {
        public List<TranscriptionCommand> Commands { get; } = [];
        public Func<TranscriptionCommand, CancellationToken, Task<int>>? Execute { get; init; }
        public async Task<int> RunAsync(TranscriptionCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (Execute is not null) return await Execute(command, cancellationToken);
            if (command.Arguments.Contains("--output-txt")) await File.WriteAllTextAsync(command.OutputFile, "Synthetic words", cancellationToken);
            else await File.WriteAllBytesAsync(command.OutputFile, Wave(), cancellationToken);
            return 0;
        }
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SocialTelemetry.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    public void Dispose()
    {
        var absolute = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests")) + Path.DirectorySeparatorChar, absolute);
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
