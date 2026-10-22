using SocialTelemetry.Application.Contracts.Profiles;
namespace SocialTelemetry.Infrastructure.Profiles;

// The local host has one active owner. Persisted checks remain authoritative at commit.
public sealed class LocalProfileLifetimes(ILogger<LocalProfileLifetimes>? logger = null, IHostApplicationLifetime? host = null) : IDisposable
{
    private readonly object gate = new();
    private Guid? version;
    private CancellationTokenSource current = CancellationTokenSource.CreateLinkedTokenSource(host?.ApplicationStopping ?? CancellationToken.None);
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
        CancelRetired(Replace(newVersion));
    }

    internal CancellationTokenSource? Replace(Guid newVersion)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (version == newVersion) return null;
            var previous = current;
            current = CancellationTokenSource.CreateLinkedTokenSource(host?.ApplicationStopping ?? CancellationToken.None);
            version = newVersion;
            return previous;
        }
    }

    internal void CancelRetired(CancellationTokenSource? retired)
    {
        if (retired is not null) CancelAndDispose(retired);
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
