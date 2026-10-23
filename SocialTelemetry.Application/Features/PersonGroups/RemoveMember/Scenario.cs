using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups.RemoveMember;

public sealed class Scenario(IPersonGroupsPersistence persistence, LocalProfileOperation operation)
{
    public async Task ExecuteAsync(Guid groupId, Guid personId, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        await persistence.ChangeMembershipAsync(owner, groupId, personId, exists => false, admission.CancellationToken);
    }
}
