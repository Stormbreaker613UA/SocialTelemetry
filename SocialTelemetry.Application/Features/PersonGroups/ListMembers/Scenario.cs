using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups.ListMembers;

public sealed class Scenario(IPersonGroupsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<IReadOnlyList<GroupMember>> ExecuteAsync(Guid groupId, bool? archived, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        return await persistence.MembersAsync(owner, groupId, archived, admission.CancellationToken);
    }
}
