namespace SocialTelemetry.Application.Features.AiConnection.Status;

public sealed class Scenario(IProfileAiManagement connection)
{
    public Task<ConnectionStatus> ExecuteAsync(CancellationToken cancellationToken) => connection.StatusAsync(cancellationToken);
}
