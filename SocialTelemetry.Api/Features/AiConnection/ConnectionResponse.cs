using SocialTelemetry.Application.Features.AiConnection;

namespace SocialTelemetry.Api.Features.AiConnection;

public sealed record AccountResponse(Guid Id, string Label, bool Connected);
public sealed record ConnectionResponse(bool Connected, bool InferencePermissionAvailable, Guid? ConnectionId,
    string? SelectedModel, IReadOnlyList<AccountResponse> Accounts)
{
    public static ConnectionResponse From(ConnectionStatus status) => new(status.Connected,
        status.InferencePermissionAvailable, status.ConnectionId, status.SelectedModel,
        status.Accounts.Select(account => new AccountResponse(account.Id, account.Label, account.Connected)).ToArray());
}
public sealed record CapabilityResponse(string Text, string Vision, string StructuredOutput);
public sealed record ModelResponse(string Id, string DisplayName)
{
    public required CapabilityResponse CapabilitySupport { get; init; }
}
public sealed record ModelCatalogResponse(IReadOnlyList<ModelResponse> Models, string? SelectedModel, bool SelectedModelAvailable)
{
    public static ModelCatalogResponse From(ModelCatalog catalog) => new(
        catalog.Models.Select(model => new ModelResponse(model.Id, model.DisplayName)
        {
            CapabilitySupport = new CapabilityResponse(model.CapabilitySupport.Text.ToString(),
                model.CapabilitySupport.Vision.ToString(), model.CapabilitySupport.StructuredOutput.ToString())
        }).ToArray(),
        catalog.SelectedModel, catalog.SelectedModelAvailable);
}
public sealed record DisconnectResponse(bool RemoteRevocationConfirmed, string Message);
