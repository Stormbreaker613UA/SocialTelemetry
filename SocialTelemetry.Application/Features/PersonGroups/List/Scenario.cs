using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups.List;

public sealed class Scenario(IPersonGroupsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<IReadOnlyList<PersonGroup>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        return await persistence.ListAsync(owner, admission.CancellationToken);
    }
}
