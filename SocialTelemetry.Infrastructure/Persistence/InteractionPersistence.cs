using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Features.Interactions;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Infrastructure.Storage;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class InteractionPersistence(AppDbContext database, IAttachmentStorage storage) : IInteractionPersistence
{
    public Task<bool> ProfileExistsAsync(Guid ownerId, Guid profileId, CancellationToken cancellationToken) =>
        database.UserProfiles.AsNoTracking().AnyAsync(profile => profile.Id == ownerId && profile.Id == profileId, cancellationToken);

    public Task<int> CountParticipantsAsync(Guid ownerId, Guid profileId, IReadOnlyList<Guid> personIds, CancellationToken cancellationToken) =>
        database.People.AsNoTracking().CountAsync(person => person.UserProfileId == ownerId &&
            person.UserProfileId == profileId && personIds.Contains(person.Id), cancellationToken);

    public Task<bool> PersonExistsAsync(Guid ownerId, Guid personId, CancellationToken cancellationToken) =>
        database.People.AsNoTracking().AnyAsync(person => person.UserProfileId == ownerId && person.Id == personId, cancellationToken);

    public Task<Interaction?> FindForUpdateAsync(Guid ownerId, Guid interactionId, CancellationToken cancellationToken) =>
        database.Interactions.Include(interaction => interaction.Participants)
            .SingleOrDefaultAsync(interaction => interaction.UserProfileId == ownerId && interaction.Id == interactionId, cancellationToken);

    public Task<Interaction?> FindForDeleteAsync(Guid ownerId, Guid interactionId, CancellationToken cancellationToken) =>
        database.Interactions.Include(interaction => interaction.Attachments)
            .SingleOrDefaultAsync(interaction => interaction.UserProfileId == ownerId && interaction.Id == interactionId, cancellationToken);

    public Task<InteractionDetails?> ReadInteractionAsync(Guid ownerId, Guid interactionId, CancellationToken cancellationToken) =>
        database.Interactions.AsNoTracking().Where(interaction => interaction.UserProfileId == ownerId && interaction.Id == interactionId)
            .Select(interaction => new InteractionDetails(interaction.Id, interaction.UserProfileId, interaction.Title,
                interaction.Description, interaction.UserThoughts, interaction.OccurredAt, interaction.CreatedAt,
                interaction.Participants.Where(participant => participant.Person.UserProfileId == ownerId)
                    .OrderBy(participant => participant.Person.DisplayName)
                    .Select(participant => new ParticipantDetails(participant.PersonId, participant.Person.DisplayName)).ToList()))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<InteractionSummary>> ReadForPersonAsync(Guid ownerId, Guid personId, CancellationToken cancellationToken) =>
        await database.Interactions.AsNoTracking().Where(interaction => interaction.UserProfileId == ownerId &&
                interaction.Participants.Any(participant => participant.PersonId == personId))
            .OrderByDescending(interaction => interaction.OccurredAt)
            .Select(interaction => new InteractionSummary(interaction.Id, interaction.Title, interaction.Description,
                interaction.UserThoughts, interaction.OccurredAt, interaction.CreatedAt)).ToListAsync(cancellationToken);

    public async Task AddInteractionAsync(Interaction interaction, CancellationToken cancellationToken)
    {
        database.Interactions.Add(interaction);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateInteractionAsync(Interaction interaction, IReadOnlyList<InteractionParticipant> removed,
        IReadOnlyList<InteractionParticipant> added, CancellationToken cancellationToken)
    {
        database.InteractionParticipants.RemoveRange(removed);
        database.InteractionParticipants.AddRange(added);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAttachmentsDeletingAsync(Interaction interaction, CancellationToken cancellationToken) =>
        await database.SaveChangesAsync(cancellationToken);

    public Task DeleteMarkedFileAsync(string key, CancellationToken cancellationToken) =>
        AttachmentFileCleanup.DeleteForMarkedAttachmentsAsync(database, storage, key, cancellationToken);

    public async Task DeleteInteractionAsync(Interaction interaction, CancellationToken cancellationToken)
    {
        database.Interactions.Remove(interaction);
        await database.SaveChangesAsync(cancellationToken);
    }
}
