using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.PersonFacts.GetForPerson;

public sealed class Scenario(IPersonFactsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<IReadOnlyList<PersonFact>> ExecuteAsync(Guid personId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        if (!await persistence.PersonExistsAsync(ownerId, personId, cancellationToken)) throw new NotFoundException();
        return await persistence.ListFactsAsync(ownerId, personId, cancellationToken);
    }
}
