using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.PersonFacts.Add;

public sealed class Scenario(IPersonFactsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<PersonFact> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            throw new FeatureValidationException("Value is required.");
        }

        var personExists = await persistence.PersonExistsAsync(ownerId, request.PersonId, cancellationToken);

        if (!personExists)
        {
            throw new NotFoundException();
        }

        var fact = new PersonFact
        {
            Id = Guid.NewGuid(),
            PersonId = request.PersonId,
            Value = request.Value.Trim(),
            Source = request.Source,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await persistence.AddFactAsync(fact, cancellationToken);

        return fact;
    }
}
