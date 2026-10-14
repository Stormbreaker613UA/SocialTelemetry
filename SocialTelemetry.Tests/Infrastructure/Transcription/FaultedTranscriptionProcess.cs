using System.ComponentModel;
using SocialTelemetry.Api.Infrastructure.Transcription;

namespace SocialTelemetry.Tests.Infrastructure.Transcription;

// Deterministic faults at the existing owned-process boundary, not a substitute for native tests.
internal sealed class FaultedTranscriptionProcess(string faultSite, Exception failure, string teardown) : OwnedTranscriptionProcess
{
    private readonly TaskCompletionSource terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int exitChecks;
    private int readsFinished;
    private int terminationsFinished;
    private int disposals;

    public override bool TryGetExitCode(out int exitCode)
    {
        exitCode = 0;
        if (terminated.Task.IsCompleted) return true;
        var checks = Interlocked.Increment(ref exitChecks);
        // Monitor starts first; the second check belongs to WaitForExitAsync.
        if (faultSite == "monitor" || (faultSite == "execution" && checks == 2)) throw failure;
        return false;
    }

    public override async Task<int> ReadAsync(bool standardError, byte[] buffer, int pollMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            if (faultSite == (standardError ? "stderr" : "stdout")) throw failure;
            await terminated.Task.WaitAsync(cancellationToken);
            return 0;
        }
        finally { Interlocked.Increment(ref readsFinished); }
    }

    public override async Task TerminateAsync(int pollMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            if (teardown == "exception") throw new Win32Exception("synthetic termination failure");
            if (teardown == "timeout") await Task.Delay(Timeout.Infinite, cancellationToken);
            terminated.TrySetResult();
        }
        finally { Interlocked.Increment(ref terminationsFinished); }
    }

    public override void Dispose() => Interlocked.Increment(ref disposals);

    public void AssertReleased()
    {
        Assert.Equal(2, Volatile.Read(ref readsFinished));
        Assert.Equal(1, Volatile.Read(ref terminationsFinished));
        Assert.Equal(1, Volatile.Read(ref disposals));
    }

    public static TranscriptionCommand Command()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
        return new("unused-synthetic-executable", directory, [], Path.Combine(directory, "unused-output"), 1024);
    }
}
