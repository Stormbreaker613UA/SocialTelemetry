using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.Update;

public sealed class Scenario(IInteractionPersistence persistence, LocalProfileOperation operation)
{
    public async Task<Interaction> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        var participantIds = request.ParticipantIds?.Distinct().ToList() ?? [];

        if (string.IsNullOrWhiteSpace(request.Title) ||
            string.IsNullOrWhiteSpace(request.Description) ||
            request.OccurredAt == default ||
            participantIds.Count == 0)
        {
            throw new FeatureValidationException("Title, Description, OccurredAt, and at least one participant are required.");
        }

        var interaction = await persistence.FindForUpdateAsync(ownerId, request.Id, cancellationToken);

        if (interaction is null)
        {
            throw new NotFoundException();
        }

        var existingParticipantCount = await persistence.CountParticipantsAsync(ownerId, interaction.UserProfileId, participantIds, cancellationToken);

        if (existingParticipantCount != participantIds.Count)
        {
            throw new NotFoundException();
        }

        interaction.Title = request.Title.Trim();
        interaction.Description = request.Description.Trim();
        interaction.UserThoughts = request.UserThoughts;
        interaction.OccurredAt = request.OccurredAt.ToUniversalTime();

        var removedParticipants = interaction.Participants
            .Where(participant => !participantIds.Contains(participant.PersonId))
            .ToList();

        var existingParticipantIds = interaction.Participants
            .Select(participant => participant.PersonId)
            .ToHashSet();
        var addedParticipants = participantIds
            .Where(personId => !existingParticipantIds.Contains(personId))
            .Select(personId => new InteractionParticipant
            {
                InteractionId = interaction.Id,
                PersonId = personId
            })
            .ToList();

        await persistence.UpdateInteractionAsync(interaction, removedParticipants, addedParticipants, cancellationToken);

        return interaction;
    }
}
