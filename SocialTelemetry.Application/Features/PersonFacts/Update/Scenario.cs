using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.PersonFacts.Update;

public sealed class Scenario(IPersonFactsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<PersonFact> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            throw new FeatureValidationException("Value is required.");
        }

        var fact = await persistence.FindFactAsync(ownerId, request.PersonId, request.Id, cancellationToken);

        if (fact is null)
        {
            throw new NotFoundException();
        }

        fact.Value = request.Value.Trim();
        fact.Source = request.Source;

        await persistence.SaveFactAsync(fact, cancellationToken);

        return fact;
    }
}
