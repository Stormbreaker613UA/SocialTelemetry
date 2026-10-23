using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups.Create;

public sealed class Scenario(IPersonGroupsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<PersonGroup> ExecuteAsync(string? name, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        var normalized = GroupName.Normalize(name);
        var now = DateTimeOffset.UtcNow;
        var group = new PersonGroup { Id = Guid.NewGuid(), UserProfileId = owner, Name = normalized, CreatedAt = now, UpdatedAt = now };
        await persistence.CreateAsync(group, admission.CancellationToken);
        return group;
    }
}
