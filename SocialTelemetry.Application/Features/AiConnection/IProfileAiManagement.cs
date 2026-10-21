using SocialTelemetry.Application.Contracts.AI.Models;

namespace SocialTelemetry.Application.Features.AiConnection;

public sealed record ConnectionIdentity(Guid Id, string Label, bool Connected);
public sealed record ConnectionStatus(bool Connected, bool InferencePermissionAvailable, Guid? ConnectionId,
    string? SelectedModel, IReadOnlyList<ConnectionIdentity> Accounts);
public sealed record ModelIdentity(string Id, string DisplayName)
{
    public AiModelCapabilities CapabilitySupport { get; init; } = new();
}
public sealed record ModelCatalog(IReadOnlyList<ModelIdentity> Models, string? SelectedModel, bool SelectedModelAvailable);
public sealed record DisconnectResult(bool RemoteRevocationConfirmed, string Message);

public interface IProfileAiManagement
{
    Task BeginAsync(Guid? connectionId, bool newAccount, bool requestConsent, CancellationToken cancellationToken);
    Task<ConnectionStatus> CompleteAsync(string? state, string? code, string? clientId, string? error, CancellationToken cancellationToken);
    Task<DisconnectResult> DisconnectAsync(CancellationToken cancellationToken);
    Task<ModelCatalog> ModelsAsync(CancellationToken cancellationToken);
    Task SelectConnectionAsync(Guid connectionId, bool adoptUnassigned, CancellationToken cancellationToken);
    Task SelectModelAsync(string model, CancellationToken cancellationToken);
    Task<ConnectionStatus> StatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectionIdentity>> UnassignedAsync(CancellationToken cancellationToken);
}
