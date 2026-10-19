using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Infrastructure.Profiles;

namespace SocialTelemetry.Infrastructure.AI;

public sealed partial class ChatGptConnection
{
    internal SemaphoreSlim AuthorizationGate { get; } = new(1, 1);
    internal sealed record CompletedAuthorization(ChatGptConnectionStatus Status, LocalProfileBinding? Binding,
        Guid? Generation, CancellationToken OwnerLifetime);

    private void CheckPending()
    {
        if (pending is { } attempt && attempt.ExpiresAt > clock.GetUtcNow() &&
            !attempt.OwnerLifetime.IsCancellationRequested && !attempt.SelectionLifetime.IsCancellationRequested)
            throw new AiProviderException(AiFailure.ConnectionBusy);
    }

    internal async Task EnsureCanBeginAsync(CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        CheckPending();
    }

    internal async Task<ChatGptConnectionStatus> GetOwnedStatusAsync(Guid owner, Guid? connectionId, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        return OwnedStatus(await store.ReadAsync(cancellationToken), owner, connectionId);
    }

    internal async Task<IReadOnlyList<ChatGptAccountSummary>> GetUnassignedAsync(CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        return state.Accounts.Where(account => account.UserProfileId is null)
            .Select((account, index) => new ChatGptAccountSummary(account.Id, $"Unassigned connection {index + 1}", account.Tokens is not null)).ToArray();
    }

    internal async Task<string?> ClaimAsync(Guid owner, Guid connectionId, bool adoptUnassigned, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var account = state.Accounts.SingleOrDefault(account => account.Id == connectionId)
            ?? throw new AiProviderException(AiFailure.NotConnected);
        if (account.UserProfileId != owner && (account.UserProfileId is not null || !adoptUnassigned))
            throw new AiProviderException(AiFailure.NotConnected);
        account.UserProfileId = owner;
        // No credential copy, token rotation or global-active fallback. A failed later SQL
        // publication leaves this claim exclusively owned and retryable by the same profile.
        await store.SaveAsync(state, cancellationToken);
        return account.SelectedModel;
    }

    internal async Task<ChatGptModelCatalog> GetOwnedModelsAsync(Guid owner, Guid connectionId, string? selectedModel, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var account = OwnedAccount(state, owner, connectionId);
        var models = await GetModelsAsync(state, account, cancellationToken);
        return new ChatGptModelCatalog(models, selectedModel, models.Any(model => model.Id == selectedModel));
    }

    internal async Task<InferenceSession> GetOwnedInferenceSessionAsync(Guid owner, Guid connectionId, string model, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var account = OwnedAccount(state, owner, connectionId);
        var models = await GetModelsAsync(state, account, cancellationToken);
        if (!models.Any(candidate => candidate.Id == model)) throw new AiProviderException(AiFailure.ModelUnavailable);
        var tokens = account.Tokens ?? throw new AiProviderException(AiFailure.NotConnected);
        if (tokens.SessionId == Guid.Empty)
        {
            tokens.SessionId = Guid.NewGuid();
            await store.SaveAsync(state, cancellationToken);
        }
        return new InferenceSession { Model = model, AccessToken = tokens.AccessToken, SessionId = tokens.SessionId,
            LifetimeCancellationToken = lifetimes.GetOrAdd(tokens.SessionId, _ => new CancellationTokenSource()).Token };
    }

    internal async Task ValidateOwnedSessionAsync(Guid owner, Guid connectionId, Guid sessionId, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        await ValidateLockedSessionAsync(sessionId, cancellationToken, owner, connectionId);
    }

    internal async Task<IAiExecutionLease> AcquireOwnedLeaseAsync(Guid owner, Guid connectionId, Guid sessionId,
        CancellationToken lifetime, CancellationToken cancellationToken)
    {
        var storageLock = await store.LockAsync(cancellationToken);
        var lease = new ExecutionLease(this, sessionId, lifetime, storageLock, owner, connectionId);
        try { await lease.ValidateAsync(cancellationToken); return lease; }
        catch { lease.Dispose(); throw; }
    }

    internal async Task<ChatGptDisconnectResult> DisconnectOwnedAsync(Guid owner, Guid? connectionId, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        if (pending?.OwnerBinding?.UserProfileId == owner) pending = null;
        var state = await store.ReadAsync(cancellationToken);
        var account = state.Accounts.SingleOrDefault(account => account.Id == connectionId && account.UserProfileId == owner);
        InvalidateLifetime(account?.Tokens?.SessionId);
        var confirmed = false;
        try
        {
            if (account?.Tokens?.RefreshToken is not null)
            {
                for (var attempt = 0; attempt < options.Value.RevocationAttempts; attempt++)
                {
                    try { await provider.RevokeAsync(account, cancellationToken); confirmed = true; break; }
                    catch (AiProviderException exception)
                    {
                        if (exception.Failure != AiFailure.ProviderUnavailable || attempt == options.Value.RevocationAttempts - 1) break;
                        await Task.Delay(options.Value.RevocationRetryDelay, cancellationToken);
                    }
                }
            }
        }
        finally
        {
            if (account is not null) account.Tokens = null;
            await store.SaveAsync(state, CancellationToken.None);
        }
        return new ChatGptDisconnectResult(confirmed, confirmed ? "Disconnected; remote revocation confirmed." : "Disconnected locally; remote revocation was not confirmed.");
    }

    private static ChatGptAccount OwnedAccount(ChatGptState state, Guid owner, Guid connectionId) =>
        state.Accounts.SingleOrDefault(account => account.Id == connectionId && account.UserProfileId == owner && account.Tokens is not null)
            ?? throw new AiProviderException(AiFailure.NotConnected);

    private static ChatGptConnectionStatus OwnedStatus(ChatGptState state, Guid owner, Guid? connectionId)
    {
        var account = state.Accounts.SingleOrDefault(account => account.Id == connectionId && account.UserProfileId == owner);
        return new ChatGptConnectionStatus(account?.Tokens is not null, account?.Tokens is { } tokens && HasInferencePermission(tokens),
            account?.Id, null, state.Accounts.Where(account => account.UserProfileId == owner)
                .Select((account, index) => new ChatGptAccountSummary(account.Id, $"Connection {index + 1}", account.Tokens is not null)).ToArray());
    }
}
