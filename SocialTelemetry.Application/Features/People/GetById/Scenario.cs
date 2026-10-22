using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.People.GetById;

public sealed class Scenario(IPeoplePersistence persistence, LocalProfileOperation operation)
{
    public async Task<Person> ExecuteAsync(Guid personId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        return await persistence.FindPersonAsync(ownerId, personId, true, cancellationToken) ?? throw new NotFoundException();
    }
}
