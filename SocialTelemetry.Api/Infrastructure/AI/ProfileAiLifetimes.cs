namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ProfileAiLifetimes : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, (Guid Generation, CancellationTokenSource Source)> owners = [];
    internal SemaphoreSlim Changes { get; } = new(1, 1);

    public CancellationToken Capture(Guid owner, Guid generation)
    {
        // Only a coordinated authoritative SQL read/change may publish. A consumer with a
        // stale generation must never cancel a newer execution merely by capturing its token.
        lock (gate)
        {
            if (!owners.TryGetValue(owner, out var state) || state.Generation != generation)
                throw new AiProviderException(AiFailure.ExecutionInvalidated);
            return state.Source.Token;
        }
    }

    internal CancellationTokenSource? Publish(Guid owner, Guid generation)
    {
        CancellationTokenSource? previous = null;
        lock (gate)
        {
            if (owners.TryGetValue(owner, out var state))
            {
                if (state.Generation == generation) return null;
                previous = state.Source;
            }
            owners[owner] = (generation, new CancellationTokenSource());
        }
        // The caller cancels after releasing Changes and any transaction: callbacks may
        // re-enter selection capture without running under the publication coordinator.
        return previous;
    }

    internal static void Cancel(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (AggregateException) { /* A consumer cannot roll back an already committed generation. */ }
        finally { source.Dispose(); }
    }

    public void Dispose()
    {
        CancellationTokenSource[] sources;
        lock (gate) { sources = owners.Values.Select(state => state.Source).ToArray(); owners.Clear(); }
        foreach (var source in sources) Cancel(source);
    }
}
