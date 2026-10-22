using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.Create;

public sealed class Scenario(IInteractionPersistence persistence, LocalProfileOperation operation)
{
    public async Task<Interaction> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        var participantIds = request.ParticipantIds?.Distinct().ToList() ?? [];

        if (request.UserProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Title) ||
            string.IsNullOrWhiteSpace(request.Description) ||
            request.OccurredAt == default ||
            participantIds.Count == 0)
        {
            throw new FeatureValidationException("UserProfileId, Title, Description, OccurredAt, and at least one participant are required.");
        }

        var userProfileExists = await persistence.ProfileExistsAsync(ownerId, request.UserProfileId, cancellationToken);

        if (!userProfileExists)
        {
            throw new NotFoundException();
        }

        var existingParticipantCount = await persistence.CountParticipantsAsync(ownerId, request.UserProfileId, participantIds, cancellationToken);

        if (existingParticipantCount != participantIds.Count)
        {
            throw new NotFoundException();
        }

        var interaction = new Interaction
        {
            Id = Guid.NewGuid(),
            UserProfileId = request.UserProfileId,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            UserThoughts = request.UserThoughts,
            OccurredAt = request.OccurredAt.ToUniversalTime(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        interaction.Participants = participantIds
            .Select(personId => new InteractionParticipant
            {
                InteractionId = interaction.Id,
                PersonId = personId
            })
            .ToList();

        await persistence.AddInteractionAsync(interaction, cancellationToken);

        return interaction;
    }
}
