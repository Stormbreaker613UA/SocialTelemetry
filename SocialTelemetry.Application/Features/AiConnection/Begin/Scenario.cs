namespace SocialTelemetry.Application.Features.AiConnection.Begin;

public sealed class Scenario(IProfileAiManagement connection, SocialTelemetry.Application.Contracts.Profiles.LocalProfileOperation operation)
{
    public async Task ExecuteAsync(Guid? connectionId, bool newAccount, bool requestConsent, CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        await connection.BeginAsync(connectionId, newAccount, requestConsent, cancellationToken);
    }
}
