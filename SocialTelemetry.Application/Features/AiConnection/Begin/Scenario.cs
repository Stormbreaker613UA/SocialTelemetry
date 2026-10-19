namespace SocialTelemetry.Application.Features.AiConnection.Begin;

public sealed class Scenario(IProfileAiManagement connection)
{
    public Task ExecuteAsync(Guid? connectionId, bool newAccount, bool requestConsent, CancellationToken cancellationToken) =>
        connection.BeginAsync(connectionId, newAccount, requestConsent, cancellationToken);
}
