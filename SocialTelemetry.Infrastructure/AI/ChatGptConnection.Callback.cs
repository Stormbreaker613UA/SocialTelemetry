using SocialTelemetry.Application.Contracts.AI;

namespace SocialTelemetry.Infrastructure.AI;

public sealed partial class ChatGptConnection
{
    internal sealed class BoundCallback(PendingAuthorization attempt, string code, string clientId)
    {
        public PendingAuthorization Attempt { get; } = attempt;
        public string Code { get; } = code;
        public string ClientId { get; } = clientId;
    }
    internal sealed class CallbackIdentity(ChatGptTokens tokens, string subject)
    {
        public ChatGptTokens Tokens { get; } = tokens;
        public string Subject { get; } = subject;
    }
    internal sealed record PublishedCallback(ChatGptConnectionStatus Status, Guid? PreviousSession);

    internal async Task<BoundCallback> TakeBoundCallbackAsync(string? returnedState, string? code,
        string? clientId, string? error, CancellationToken cancellationToken)
    {
        // Consume the one-use attempt without changing credentials. SQL admission must be
        // checked after releasing this file lock, before registration or any provider call.
        using var storageLock = await store.LockAsync(cancellationToken);
        var attempt = pending;
        pending = null;
        if (attempt is null || attempt.ExpiresAt <= clock.GetUtcNow() ||
            !ChatGptHttpClient.FixedEquals(attempt.State, returnedState))
            throw new AiProviderException(AiFailure.InvalidCallback);
        CheckCallbackLifetime(attempt, cancellationToken);
        if (error is not null)
            throw new AiProviderException(error == "access_denied" ? AiFailure.AuthorizationDenied : AiFailure.InvalidCallback);
        if (attempt.OwnerBinding is null || attempt.SelectionGeneration is null || string.IsNullOrWhiteSpace(code) ||
            (attempt.ClientId is not null && clientId is not null && clientId != attempt.ClientId))
            throw new AiProviderException(AiFailure.InvalidCallback);
        var issuedClientId = attempt.ClientId ?? clientId;
        if (string.IsNullOrWhiteSpace(issuedClientId) || issuedClientId == ChatGptProtocol.DynamicClient || issuedClientId.Length > 512)
            throw new AiProviderException(AiFailure.InvalidCallback);
        return new BoundCallback(attempt, code, issuedClientId);
    }

    internal async Task RegisterBoundCallbackAsync(BoundCallback callback,
        Func<CancellationToken, Task> revalidateAdmission, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        CheckCallbackLifetime(callback.Attempt, cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        // The caller already holds selector/profile protection. Recheck the deadline
        // after waiting for the file, without acquiring SQL protection in reverse order.
        await revalidateAdmission(cancellationToken);
        var account = state.Accounts.SingleOrDefault(saved => saved.Id == callback.Attempt.ConnectionId);
        if (account is null)
        {
            if (state.Accounts.Any(saved => saved.ClientId == callback.ClientId))
                throw new AiProviderException(AiFailure.InvalidCallback);
            state.Accounts.Add(new ChatGptAccount { Id = callback.Attempt.ConnectionId, ClientId = callback.ClientId,
                UserProfileId = callback.Attempt.OwnerBinding!.UserProfileId });
        }
        else CheckCallbackAccount(callback, account);
        // The caller holds authoritative admission only for this short file phase.
        // Authorized registration survives an exchange failure; existing tokens stay intact.
        await store.SaveAsync(state, cancellationToken);
    }

    internal async Task<CallbackIdentity> ExchangeBoundCallbackAsync(BoundCallback callback, CancellationToken cancellationToken)
    {
        CheckCallbackLifetime(callback.Attempt, cancellationToken);
        var tokens = await provider.ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = callback.ClientId,
            ["code"] = callback.Code, ["code_verifier"] = callback.Attempt.Verifier,
            ["redirect_uri"] = callback.Attempt.CallbackUri, ["resource"] = ChatGptProtocol.Resource
        }, refresh: false, cancellationToken);
        var subject = await provider.ValidateIdentityAsync(tokens.IdToken, callback.ClientId, callback.Attempt.Nonce, cancellationToken);
        return new CallbackIdentity(tokens, subject);
    }

    internal async Task<PublishedCallback> PublishBoundCallbackAsync(BoundCallback callback,
        CallbackIdentity identity, Func<CancellationToken, Task> revalidateAdmission, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        CheckCallbackLifetime(callback.Attempt, cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var account = state.Accounts.SingleOrDefault(saved => saved.Id == callback.Attempt.ConnectionId)
            ?? throw new AiProviderException(AiFailure.InvalidCallback);
        CheckCallbackAccount(callback, account);
        if (account.Subject is not null && account.Subject != identity.Subject)
            throw new AiProviderException(AiFailure.InvalidIdentity);
        await revalidateAdmission(cancellationToken);
        var previousSession = account.Tokens?.SessionId;
        account.Subject = identity.Subject;
        identity.Tokens.SessionId = Guid.NewGuid();
        account.Tokens = identity.Tokens;
        await store.SaveAsync(state, cancellationToken);
        logger.LogInformation("ChatGPT connection {ConnectionId} authorized; inference permission {InferencePermission}",
            account.Id, HasInferencePermission(identity.Tokens));
        return new PublishedCallback(OwnedStatus(state, callback.Attempt.OwnerBinding!.UserProfileId, account.Id), previousSession);
    }

    // Called only after the caller has released both SQL protection and the file lock.
    internal void RetireCallbackSession(PublishedCallback published) => InvalidateLifetime(published.PreviousSession);

    private static void CheckCallbackAccount(BoundCallback callback, ChatGptAccount account)
    {
        if (account.ClientId != callback.ClientId || account.UserProfileId != callback.Attempt.OwnerBinding!.UserProfileId)
            throw new AiProviderException(AiFailure.InvalidCallback);
    }

    private static void CheckCallbackLifetime(PendingAuthorization attempt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attempt.OwnerLifetime.IsCancellationRequested || attempt.SelectionLifetime.IsCancellationRequested)
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
    }
}
