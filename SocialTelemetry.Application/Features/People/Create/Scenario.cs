using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.People.Create;

public sealed class Scenario(IPeoplePersistence persistence, LocalProfileOperation operation)
{
    public async Task<Person> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        if (request.UserProfileId == Guid.Empty || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new FeatureValidationException("UserProfileId and DisplayName are required.");
        }

        if (request.DisplayName.Trim().Length > 200 || !Enum.IsDefined(request.RelationshipContext))
        {
            throw new FeatureValidationException("DisplayName cannot exceed 200 characters and RelationshipContext must be supported.");
        }

        var userProfileExists = await persistence.ProfileExistsAsync(ownerId, request.UserProfileId, cancellationToken);

        if (!userProfileExists)
        {
            throw new NotFoundException();
        }

        var now = DateTimeOffset.UtcNow;
        var person = new Person
        {
            Id = Guid.NewGuid(),
            UserProfileId = request.UserProfileId,
            DisplayName = request.DisplayName.Trim(),
            Age = request.Age,
            Gender = request.Gender,
            Description = request.Description,
            RelationshipContext = request.RelationshipContext,
            HowWeMet = request.HowWeMet,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now
        };

        await persistence.AddPersonAsync(person, cancellationToken);

        return person;
    }
}
