using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.People.Update;

public sealed class Scenario(IPeoplePersistence persistence, LocalProfileOperation operation)
{
    public async Task<Person> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new FeatureValidationException("DisplayName is required.");
        }

        if (request.DisplayName.Trim().Length > 200 || !Enum.IsDefined(request.RelationshipContext))
        {
            throw new FeatureValidationException("DisplayName cannot exceed 200 characters and RelationshipContext must be supported.");
        }

        var person = await persistence.FindPersonAsync(ownerId, request.Id, false, cancellationToken);

        if (person is null)
        {
            throw new NotFoundException();
        }

        person.DisplayName = request.DisplayName.Trim();
        person.Age = request.Age;
        person.Gender = request.Gender;
        person.Description = request.Description;
        person.RelationshipContext = request.RelationshipContext;
        person.HowWeMet = request.HowWeMet;
        person.Notes = request.Notes;
        person.UpdatedAt = DateTimeOffset.UtcNow;

        await persistence.SavePersonAsync(person, cancellationToken);

        return person;
    }
}
