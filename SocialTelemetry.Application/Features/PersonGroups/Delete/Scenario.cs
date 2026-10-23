using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups.Delete;

public sealed class Scenario(IPersonGroupsPersistence persistence, LocalProfileOperation operation)
{
    public async Task ExecuteAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        await persistence.DeleteAsync(owner, groupId, admission.CancellationToken);
    }
}
