using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.People.GetAll;

public sealed class Scenario(IPeoplePersistence persistence, LocalProfileOperation operation)
{
    public async Task<IReadOnlyList<Person>> ExecuteAsync(Guid profileId, bool? archived, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        if (profileId != Guid.Empty && profileId != ownerId) throw new NotFoundException();
        return await persistence.ListPeopleAsync(ownerId, archived, cancellationToken);
    }
}
