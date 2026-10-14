using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.Transcription;

namespace SocialTelemetry.Tests.Infrastructure.Transcription;

public sealed class TranscriptionCleanupTests
{
    public static TheoryData<string, bool, string> Faults
    {
        get
        {
            var cases = new TheoryData<string, bool, string>();
            foreach (var site in new[] { "stdout", "stderr", "monitor", "execution" })
                foreach (var nativeError in new[] { false, true })
                    foreach (var teardown in new[] { "timeout", "exception", "success" })
                        cases.Add(site, nativeError, teardown);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task Cleanup_uncertainty_takes_precedence_but_successful_cleanup_preserves_failure(
        string site, bool nativeError, string teardown)
    {
        Exception original = nativeError ? new Win32Exception("private synthetic diagnostic") : new IOException("private synthetic diagnostic");
        var process = new FaultedTranscriptionProcess(site, original, teardown);
        var runner = new TranscriptionProcessRunner(Options.Create(new WhisperCppOptions { TimeoutSeconds = 1, FileCheckMilliseconds = 50 }));
        var elapsed = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(async () => await runner.RunAsync(FaultedTranscriptionProcess.Command(), process,
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        if (teardown != "success")
        {
            var safe = Assert.IsType<SpeechToTextException>(failure);
            Assert.Equal(SpeechToTextFailure.CleanupFailed, safe.Failure);
            Assert.DoesNotContain(original.Message, safe.ToString());
        }
        else if (nativeError)
            Assert.Equal(SpeechToTextFailure.TranscriptionFailed, Assert.IsType<SpeechToTextException>(failure).Failure);
        else Assert.Same(original, failure);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4));
        process.AssertReleased();
    }
}
