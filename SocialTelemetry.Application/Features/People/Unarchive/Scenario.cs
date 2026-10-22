using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.People.Unarchive;

public sealed class Scenario(IPeoplePersistence persistence, LocalProfileOperation operation)
{
    public async Task<Person> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        var person = await persistence.FindPersonAsync(ownerId, request.Id, false, cancellationToken);
        if (person is null)
        {
            throw new NotFoundException();
        }
        person.ArchivedAt = null;
        await persistence.SavePersonAsync(person, cancellationToken);
        return person;
    }
}
