namespace SocialTelemetry.Application.Features.AiConnection.Status;

public sealed class Scenario(IProfileAiManagement connection, SocialTelemetry.Application.Contracts.Profiles.LocalProfileOperation operation)
{
    public async Task<ConnectionStatus> ExecuteAsync(CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        return await connection.StatusAsync(cancellationToken);
    }
}
