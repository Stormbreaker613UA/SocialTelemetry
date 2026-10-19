namespace SocialTelemetry.Application.Features.AiConnection.Disconnect;

public sealed class Scenario(IProfileAiManagement connection)
{
    public Task<DisconnectResult> ExecuteAsync(CancellationToken cancellationToken) => connection.DisconnectAsync(cancellationToken);
}
