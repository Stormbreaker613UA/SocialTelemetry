using SocialTelemetry.Application.Features.AiConnection;

namespace SocialTelemetry.Infrastructure.AI;

// Provider DTOs terminate here; session/generation/file coordination stays in its existing adapter.
public sealed class ProfileAiManagement(ProfileAiAccess access) : IProfileAiManagement
{
    public Task BeginAsync(Guid? connectionId, bool newAccount, bool requestConsent, CancellationToken cancellationToken) =>
        access.BeginAsync(connectionId, newAccount, requestConsent, cancellationToken);

    public async Task<ConnectionStatus> CompleteAsync(string? state, string? code, string? clientId, string? error, CancellationToken cancellationToken) =>
        Map(await access.CompleteAsync(state, code, clientId, error, cancellationToken));

    public async Task<DisconnectResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        var result = await access.DisconnectAsync(cancellationToken);
        return new DisconnectResult(result.RemoteRevocationConfirmed, result.Message);
    }

    public async Task<ModelCatalog> ModelsAsync(CancellationToken cancellationToken)
    {
        var catalog = await access.ModelsAsync(cancellationToken);
        return new ModelCatalog(catalog.Models.Select(model => new ModelIdentity(model.Id, model.DisplayName)).ToArray(),
            catalog.SelectedModel, catalog.SelectedModelAvailable);
    }

    public Task SelectConnectionAsync(Guid connectionId, bool adoptUnassigned, CancellationToken cancellationToken) =>
        access.SelectConnectionAsync(connectionId, adoptUnassigned, cancellationToken);

    public Task SelectModelAsync(string model, CancellationToken cancellationToken) =>
        access.SelectModelAsync(model, cancellationToken);

    public async Task<ConnectionStatus> StatusAsync(CancellationToken cancellationToken) =>
        Map(await access.StatusAsync(cancellationToken));

    public async Task<IReadOnlyList<ConnectionIdentity>> UnassignedAsync(CancellationToken cancellationToken) =>
        (await access.UnassignedAsync(cancellationToken)).Select(Map).ToArray();

    private static ConnectionStatus Map(ChatGptConnectionStatus status) =>
        new(status.Connected, status.InferencePermissionAvailable, status.ConnectionId, status.SelectedModel,
            status.Accounts.Select(Map).ToArray());

    private static ConnectionIdentity Map(ChatGptAccountSummary account) => new(account.Id, account.Label, account.Connected);
}
