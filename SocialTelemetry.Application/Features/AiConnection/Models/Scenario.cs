namespace SocialTelemetry.Application.Features.AiConnection.Models;

public sealed class Scenario(IProfileAiManagement connection, SocialTelemetry.Application.Contracts.Profiles.LocalProfileOperation operation)
{
    public async Task<ModelCatalog> ExecuteAsync(CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        return await connection.ModelsAsync(cancellationToken);
    }
}
