using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups.Get;

public sealed class Scenario(IPersonGroupsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<PersonGroup> ExecuteAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        return await persistence.GetAsync(owner, groupId, admission.CancellationToken);
    }
}
