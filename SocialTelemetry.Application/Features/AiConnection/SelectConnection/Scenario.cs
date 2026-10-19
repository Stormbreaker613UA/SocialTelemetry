namespace SocialTelemetry.Application.Features.AiConnection.SelectConnection;

public sealed class Scenario(IProfileAiManagement connection)
{
    public Task ExecuteAsync(Guid connectionId, bool adoptUnassigned, CancellationToken cancellationToken) =>
        connection.SelectConnectionAsync(connectionId, adoptUnassigned, cancellationToken);
}
