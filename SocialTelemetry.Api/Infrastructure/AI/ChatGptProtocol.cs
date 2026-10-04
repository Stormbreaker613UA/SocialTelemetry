namespace SocialTelemetry.Api.Infrastructure.AI;

internal static class ChatGptProtocol
{
    // Contract verified against developers.openai.com/siwc/token-sharing-open-source on 2026-10-04.
    public const string Issuer = "https://auth.openai.com";
    public const string Discovery = Issuer + "/.well-known/openid-configuration";
    public const string Authorize = Issuer + "/api/accounts/authorize";
    public const string Token = Issuer + "/api/accounts/oauth/token";
    public const string Resource = "https://api.openai.com/v1";
    public const string DynamicClient = "dynamic_agent_client";
    public const string DirectScope = "chatgpt.tokens.use.direct";
    public const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
}

internal sealed class ChatGptState
{
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public Guid? ActiveConnectionId { get; set; }
    public List<ChatGptAccount> Accounts { get; set; } = [];
}

internal sealed class ChatGptAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ClientId { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string? SelectedModel { get; set; }
    public ChatGptTokens? Tokens { get; set; }
}

// Classes intentionally avoid generated record ToString methods for credential-bearing values.
internal sealed class ChatGptTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed record ChatGptAccountSummary(Guid Id, string Label, bool Connected);
public sealed record ChatGptConnectionStatus(
    bool Connected, bool InferencePermissionAvailable, Guid? ConnectionId, string? SelectedModel,
    IReadOnlyList<ChatGptAccountSummary> Accounts);
public sealed record ChatGptModel(string Id, string DisplayName);
public sealed record ChatGptModelCatalog(IReadOnlyList<ChatGptModel> Models, string? SelectedModel, bool SelectedModelAvailable);
public sealed record ChatGptDisconnectResult(bool RemoteRevocationConfirmed, string Message);
