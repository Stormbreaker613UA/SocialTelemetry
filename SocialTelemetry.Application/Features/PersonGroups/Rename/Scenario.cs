using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups.Rename;

public sealed class Scenario(IPersonGroupsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<PersonGroup> ExecuteAsync(Guid groupId, string? name, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        var normalized = GroupName.Normalize(name);
        return await persistence.RenameAsync(owner, groupId, group =>
        {
            if (group.Name == normalized) return;
            group.Name = normalized;
            group.UpdatedAt = DateTimeOffset.UtcNow;
        }, admission.CancellationToken);
    }
}
