namespace SocialTelemetry.Application.Features.AiConnection.Unassigned;

public sealed class Scenario(IProfileAiManagement connection)
{
    public Task<IReadOnlyList<ConnectionIdentity>> ExecuteAsync(CancellationToken cancellationToken) => connection.UnassignedAsync(cancellationToken);
}
