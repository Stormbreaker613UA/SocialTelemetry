namespace SocialTelemetry.Application.Features.AiConnection.Unassigned;

public sealed class Scenario(IProfileAiManagement connection, SocialTelemetry.Application.Contracts.Profiles.LocalProfileOperation operation)
{
    public async Task<IReadOnlyList<ConnectionIdentity>> ExecuteAsync(CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        return await connection.UnassignedAsync(cancellationToken);
    }
}
