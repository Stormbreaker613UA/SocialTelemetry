using SocialTelemetry.Application.Contracts.Profiles;
namespace SocialTelemetry.Infrastructure.Profiles;

// The local host has one active owner. Persisted checks remain authoritative at commit.
public sealed class LocalProfileLifetimes(ILogger<LocalProfileLifetimes>? logger = null) : IDisposable
{
    private readonly object gate = new();
    private Guid? version;
    private CancellationTokenSource current = new();
    private bool disposed;
    internal SemaphoreSlim SelectionGate { get; } = new(1, 1);

    public CancellationToken Capture(Guid expectedVersion)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            version ??= expectedVersion;
            if (version != expectedVersion)
                throw new LocalProfileAccessException(LocalProfileFailure.SelectionChanged);
            return current.Token;
        }
    }

    public void Publish(Guid newVersion)
    {
        CancellationTokenSource previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (version == newVersion) return;
            previous = current;
            current = new CancellationTokenSource();
            version = newVersion;
        }
        // Cancellation callbacks run outside our gate and outside any database transaction.
        CancelAndDispose(previous);
    }

    public void Dispose()
    {
        CancellationTokenSource previous;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            previous = current;
        }
        CancelAndDispose(previous);
        SelectionGate.Dispose();
    }

    private void CancelAndDispose(CancellationTokenSource previous)
    {
        try { previous.Cancel(); }
        catch (AggregateException)
        {
            // A consumer callback cannot turn an already committed selection into a failed switch.
            logger?.LogWarning("A local profile cancellation callback failed");
        }
        finally { previous.Dispose(); }
    }
}
