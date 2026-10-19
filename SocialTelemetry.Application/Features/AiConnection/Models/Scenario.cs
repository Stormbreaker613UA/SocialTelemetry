namespace SocialTelemetry.Application.Features.AiConnection.Models;

public sealed class Scenario(IProfileAiManagement connection)
{
    public Task<ModelCatalog> ExecuteAsync(CancellationToken cancellationToken) => connection.ModelsAsync(cancellationToken);
}
