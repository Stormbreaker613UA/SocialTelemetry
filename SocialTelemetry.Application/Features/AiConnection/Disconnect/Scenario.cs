namespace SocialTelemetry.Application.Features.AiConnection.Disconnect;

public sealed class Scenario(IProfileAiManagement connection, SocialTelemetry.Application.Contracts.Profiles.LocalProfileOperation operation)
{
    public async Task<DisconnectResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        return await connection.DisconnectAsync(cancellationToken);
    }
}
