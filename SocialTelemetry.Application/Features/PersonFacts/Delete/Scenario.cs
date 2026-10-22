using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.PersonFacts.Delete;

public sealed class Scenario(IPersonFactsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<PersonFact> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        var fact = await persistence.FindFactAsync(ownerId, request.PersonId, request.Id, cancellationToken);

        if (fact is null)
        {
            throw new NotFoundException();
        }

        await persistence.DeleteFactAsync(fact, cancellationToken);

        return fact;
    }
}
