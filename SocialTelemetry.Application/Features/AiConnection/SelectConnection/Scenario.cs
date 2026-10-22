namespace SocialTelemetry.Application.Features.AiConnection.SelectConnection;

public sealed class Scenario(IProfileAiManagement connection, SocialTelemetry.Application.Contracts.Profiles.LocalProfileOperation operation)
{
    public async Task ExecuteAsync(Guid connectionId, bool adoptUnassigned, CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        await connection.SelectConnectionAsync(connectionId, adoptUnassigned, cancellationToken);
    }
}
