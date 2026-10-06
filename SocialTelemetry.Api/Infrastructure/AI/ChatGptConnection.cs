using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ChatGptConnection(
    ChatGptCredentialStore store,
    ChatGptHttpClient provider,
    ISystemBrowser browser,
    IOptions<ChatGptOptions> options,
    TimeProvider clock,
    ILogger<ChatGptConnection> logger)
{
    private PendingAuthorization? pending;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> lifetimes = new();

    public async Task BeginAsync(Guid? connectionId, bool newAccount, bool requestConsent, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var selectedId = connectionId ?? state.ActiveConnectionId;
        if (selectedId is null && state.Accounts.Count == 1) selectedId = state.Accounts[0].Id;
        var account = newAccount ? null : state.Accounts.SingleOrDefault(account => account.Id == selectedId);
        if (connectionId is not null && (newAccount || account is null))
            throw new AiProviderException(AiFailure.InvalidCallback);

        var attempt = new PendingAuthorization
        {
            State = RandomValue(),
            Nonce = RandomValue(),
            Verifier = RandomValue(),
            ExpiresAt = clock.GetUtcNow().AddMinutes(10),
            ConnectionId = account?.Id ?? Guid.NewGuid(),
            ClientId = account?.ClientId,
            CallbackUri = options.Value.CallbackUri
        };
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = account?.ClientId ?? ChatGptProtocol.DynamicClient,
            ["ext_agent_host_id"] = state.HostId,
            ["response_type"] = "code",
            ["redirect_uri"] = attempt.CallbackUri,
            ["scope"] = ChatGptProtocol.Scopes,
            ["resource"] = ChatGptProtocol.Resource,
            ["state"] = attempt.State,
            ["nonce"] = attempt.Nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier)))
        };
        if (account is null) parameters["agent_name_hint"] = "SocialTelemetry";
        if (account?.Tokens?.IdToken is { } idToken) parameters["id_token_hint"] = idToken;
        if (requestConsent) parameters["prompt"] = "consent";
        await store.SaveAsync(state, cancellationToken);
        pending = attempt;
        try
        {
            browser.Open(new Uri(QueryHelpers.AddQueryString(ChatGptProtocol.Authorize, parameters)));
        }
        catch
        {
            pending = null;
            throw;
        }
        logger.LogInformation("ChatGPT authorization started");
    }

    public async Task<ChatGptConnectionStatus> CompleteAsync(
        string? returnedState, string? code, string? clientId, string? error, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var attempt = pending;
        pending = null;
        if (attempt is null || attempt.ExpiresAt <= clock.GetUtcNow() || !ChatGptHttpClient.FixedEquals(attempt.State, returnedState))
            throw new AiProviderException(AiFailure.InvalidCallback);
        if (error is not null)
            throw new AiProviderException(error == "access_denied" ? AiFailure.AuthorizationDenied : AiFailure.InvalidCallback);
        if (string.IsNullOrWhiteSpace(code) ||
            (attempt.ClientId is not null && clientId is not null && clientId != attempt.ClientId))
            throw new AiProviderException(AiFailure.InvalidCallback);
        var issuedClientId = attempt.ClientId ?? clientId;
        if (string.IsNullOrWhiteSpace(issuedClientId) || issuedClientId == ChatGptProtocol.DynamicClient || issuedClientId.Length > 512)
            throw new AiProviderException(AiFailure.InvalidCallback);

        var state = await store.ReadAsync(cancellationToken);
        var account = state.Accounts.SingleOrDefault(account => account.Id == attempt.ConnectionId);
        if (account is null)
        {
            if (state.Accounts.Any(existing => existing.ClientId == issuedClientId))
                throw new AiProviderException(AiFailure.InvalidCallback);
            account = new ChatGptAccount { Id = attempt.ConnectionId, ClientId = issuedClientId };
            state.Accounts.Add(account);
        }
        else if (account.ClientId != issuedClientId)
        {
            throw new AiProviderException(AiFailure.InvalidCallback);
        }
        // Preserve registration even if the one-use code expires or the exchange fails.
        await store.SaveAsync(state, cancellationToken);
        var tokens = await provider.ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = issuedClientId,
            ["code"] = code,
            ["code_verifier"] = attempt.Verifier,
            ["redirect_uri"] = attempt.CallbackUri,
            ["resource"] = ChatGptProtocol.Resource
        }, refresh: false, cancellationToken);
        var subject = await provider.ValidateIdentityAsync(tokens.IdToken, issuedClientId, attempt.Nonce, cancellationToken);
        if (account.Subject is not null && account.Subject != subject)
            throw new AiProviderException(AiFailure.InvalidIdentity);
        account.Subject = subject;
        var previousAccount = state.Accounts.SingleOrDefault(saved => saved.Id == state.ActiveConnectionId);
        InvalidateLifetime(previousAccount?.Tokens?.SessionId);
        tokens.SessionId = Guid.NewGuid();
        account.Tokens = tokens;
        state.ActiveConnectionId = account.Id;
        await store.SaveAsync(state, cancellationToken);
        logger.LogInformation("ChatGPT connection {ConnectionId} authorized; inference permission {InferencePermission}",
            account.Id, HasInferencePermission(tokens));
        return Status(state);
    }

    public async Task<ChatGptConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        return Status(await store.ReadAsync(cancellationToken));
    }

    public async Task<ChatGptModelCatalog> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var account = ActiveAccount(state);
        var models = await GetModelsAsync(state, account, cancellationToken);
        return new ChatGptModelCatalog(models, account.SelectedModel, models.Any(model => model.Id == account.SelectedModel));
    }

    public async Task SelectModelAsync(string modelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new AiProviderException(AiFailure.ModelNotSelected);
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var account = ActiveAccount(state);
        var models = await GetModelsAsync(state, account, cancellationToken);
        if (!models.Any(model => model.Id == modelId)) throw new AiProviderException(AiFailure.ModelUnavailable);
        account.SelectedModel = modelId;
        await store.SaveAsync(state, cancellationToken);
        logger.LogInformation("ChatGPT model {ModelId} selected for connection {ConnectionId}", modelId, account.Id);
    }

    public async Task<ChatGptDisconnectResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        pending = null;
        var state = await store.ReadAsync(cancellationToken);
        var account = state.Accounts.SingleOrDefault(account => account.Id == state.ActiveConnectionId);
        var confirmed = false;
        InvalidateLifetime(account?.Tokens?.SessionId);
        try
        {
            if (account?.Tokens?.RefreshToken is not null)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await provider.RevokeAsync(account, cancellationToken);
                        confirmed = true;
                        break;
                    }
                    catch (AiProviderException exception)
                    {
                        if (exception.Failure != AiFailure.ProviderUnavailable || attempt == 1) break;
                        await Task.Delay(200, cancellationToken);
                    }
                }
            }
        }
        finally
        {
            if (account is not null) account.Tokens = null;
            await store.SaveAsync(state, CancellationToken.None);
        }
        logger.LogInformation("ChatGPT connection disconnected; remote revocation confirmed {RevocationConfirmed}", confirmed);
        return new ChatGptDisconnectResult(confirmed, confirmed
            ? "Disconnected. The renewable session was revoked."
            : "Disconnected locally. Remote revocation was not confirmed; remove app access in ChatGPT Settings if needed.");
    }

    internal async Task<InferenceSession> GetInferenceSessionAsync(CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var account = ActiveAccount(state);
        if (account.SelectedModel is null) throw new AiProviderException(AiFailure.ModelNotSelected);
        var models = await GetModelsAsync(state, account, cancellationToken);
        if (!models.Any(model => model.Id == account.SelectedModel)) throw new AiProviderException(AiFailure.ModelUnavailable);
        var tokens = account.Tokens ?? throw new AiProviderException(AiFailure.NotConnected);
        // Older protected stores predate session generations. Persist one before issuing a lease.
        if (tokens.SessionId == Guid.Empty)
        {
            tokens.SessionId = Guid.NewGuid();
            await store.SaveAsync(state, cancellationToken);
        }
        var lifetime = lifetimes.GetOrAdd(tokens.SessionId, _ => new CancellationTokenSource());
        return new InferenceSession
        {
            AccessToken = tokens.AccessToken, Model = account.SelectedModel,
            SessionId = tokens.SessionId, LifetimeCancellationToken = lifetime.Token
        };
    }

    internal async Task ValidateSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        using var storageLock = await store.LockAsync(cancellationToken);
        var state = await store.ReadAsync(cancellationToken);
        var account = state.Accounts.SingleOrDefault(saved => saved.Id == state.ActiveConnectionId);
        if (account?.Tokens?.SessionId != sessionId)
        {
            InvalidateLifetime(sessionId);
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        }
    }

    private void InvalidateLifetime(Guid? sessionId)
    {
        if (sessionId is { } id && lifetimes.TryRemove(id, out var lifetime))
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    private async Task<IReadOnlyList<ChatGptModel>> GetModelsAsync(ChatGptState state, ChatGptAccount account, CancellationToken cancellationToken)
    {
        var tokens = await GetTokensAsync(state, account, forceRefresh: false, cancellationToken);
        try
        {
            return await provider.GetModelsAsync(tokens.AccessToken, cancellationToken);
        }
        catch (AiProviderException exception) when (exception.Failure == AiFailure.ReconnectRequired)
        {
            tokens = await GetTokensAsync(state, account, forceRefresh: true, cancellationToken);
            return await provider.GetModelsAsync(tokens.AccessToken, cancellationToken);
        }
    }

    private async Task<ChatGptTokens> GetTokensAsync(ChatGptState state, ChatGptAccount account, bool forceRefresh, CancellationToken cancellationToken)
    {
        var tokens = account.Tokens ?? throw new AiProviderException(AiFailure.NotConnected);
        if (!HasInferencePermission(tokens)) throw new AiProviderException(AiFailure.InferencePermissionMissing);
        if (!forceRefresh && tokens.ExpiresAt > clock.GetUtcNow()) return tokens;
        if (tokens.RefreshToken is null)
        {
            InvalidateLifetime(account.Tokens?.SessionId);
            account.Tokens = null;
            await store.SaveAsync(state, CancellationToken.None);
            throw new AiProviderException(AiFailure.ReconnectRequired);
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Once rotation starts, persist its replacement even if the caller disconnects.
        using var refreshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ChatGptTokens replacement;
        try
        {
            replacement = await provider.ExchangeAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = account.ClientId,
                ["refresh_token"] = tokens.RefreshToken,
                ["resource"] = ChatGptProtocol.Resource
            }, refresh: true, refreshTimeout.Token);
        }
        catch (AiProviderException exception) when (exception.Failure is AiFailure.ReconnectRequired or AiFailure.InvalidIdentity or AiFailure.MalformedResponse or AiFailure.InvalidClient)
        {
            InvalidateLifetime(account.Tokens?.SessionId);
            account.Tokens = null;
            await store.SaveAsync(state, CancellationToken.None);
            throw new AiProviderException(exception.Failure == AiFailure.InvalidClient ? AiFailure.InvalidClient : AiFailure.ReconnectRequired);
        }
        catch (AiProviderException) { throw new AiProviderException(AiFailure.RefreshFailed); }
        catch (OperationCanceledException) { throw new AiProviderException(AiFailure.RefreshFailed); }

        try
        {
            if (replacement.IdToken is not null)
            {
                var subject = await provider.ValidateIdentityAsync(replacement.IdToken, account.ClientId, nonce: null, refreshTimeout.Token);
                if (subject != account.Subject) throw new AiProviderException(AiFailure.InvalidIdentity);
            }
        }
        catch
        {
            // The replacement refresh token may have rotated. Never retry the consumed one.
            InvalidateLifetime(account.Tokens?.SessionId);
            account.Tokens = null;
            await store.SaveAsync(state, CancellationToken.None);
            logger.LogWarning("ChatGPT replacement identity validation failed; connection {ConnectionId} requires reconnect", account.Id);
            throw new AiProviderException(AiFailure.ReconnectRequired);
        }

        replacement.IdToken ??= tokens.IdToken;
        replacement.SessionId = tokens.SessionId;
        account.Tokens = replacement;
        await store.SaveAsync(state, CancellationToken.None);
        logger.LogInformation("ChatGPT credentials refreshed for connection {ConnectionId}", account.Id);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasInferencePermission(replacement)) throw new AiProviderException(AiFailure.InferencePermissionMissing);
        return replacement;
    }

    private static ChatGptAccount ActiveAccount(ChatGptState state) =>
        state.Accounts.SingleOrDefault(account => account.Id == state.ActiveConnectionId && account.Tokens is not null)
            ?? throw new AiProviderException(AiFailure.NotConnected);

    private static bool HasInferencePermission(ChatGptTokens tokens) =>
        tokens.Scopes.Contains(ChatGptProtocol.DirectScope, StringComparer.Ordinal) &&
        tokens.Scopes.Contains("resource.invoke", StringComparer.Ordinal);

    private static ChatGptConnectionStatus Status(ChatGptState state)
    {
        var active = state.Accounts.SingleOrDefault(account => account.Id == state.ActiveConnectionId);
        return new ChatGptConnectionStatus(active?.Tokens is not null,
            active?.Tokens is { } tokens && HasInferencePermission(tokens), active?.Id, active?.SelectedModel,
            state.Accounts.Select((account, index) => new ChatGptAccountSummary(account.Id, $"Connection {index + 1}", account.Tokens is not null)).ToList());
    }

    private static string RandomValue() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private sealed class PendingAuthorization
    {
        public required string State { get; init; }
        public required string Nonce { get; init; }
        public required string Verifier { get; init; }
        public required string CallbackUri { get; init; }
        public required Guid ConnectionId { get; init; }
        public string? ClientId { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }

    internal sealed class InferenceSession
    {
        public required string AccessToken { get; init; }
        public required string Model { get; init; }
        public required Guid SessionId { get; init; }
        public required CancellationToken LifetimeCancellationToken { get; init; }
    }
}
