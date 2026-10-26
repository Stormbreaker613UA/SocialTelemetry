using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions;

public interface IInteractionPersistence
{
    Task<bool> ProfileExistsAsync(Guid ownerId, Guid profileId, CancellationToken cancellationToken);
    Task<int> CountParticipantsAsync(Guid ownerId, Guid profileId, IReadOnlyList<Guid> personIds, CancellationToken cancellationToken);
    Task<bool> PersonExistsAsync(Guid ownerId, Guid personId, CancellationToken cancellationToken);
    Task<Interaction?> FindForUpdateAsync(Guid ownerId, Guid interactionId, CancellationToken cancellationToken);
    Task<Interaction?> FindForDeleteAsync(Guid ownerId, Guid interactionId, CancellationToken cancellationToken);
    Task<InteractionDetails?> ReadInteractionAsync(Guid ownerId, Guid interactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InteractionSummary>> ReadForPersonAsync(Guid ownerId, Guid personId, CancellationToken cancellationToken);
    Task AddInteractionAsync(Interaction interaction, CancellationToken cancellationToken);
    Task UpdateInteractionAsync(Interaction interaction, IReadOnlyList<InteractionParticipant> removed, IReadOnlyList<InteractionParticipant> added, CancellationToken cancellationToken);
    Task PrepareDeletionAsync(Interaction interaction, Action<Interaction> markAttachmentsDeleting, CancellationToken cancellationToken);
    Task DeleteMarkedFileAsync(string key, CancellationToken cancellationToken);
    Task DeleteInteractionAsync(Interaction interaction, CancellationToken cancellationToken);
}

public sealed record InteractionDetails(Guid Id, Guid UserProfileId, string Title, string Description,
    string? UserThoughts, DateTimeOffset OccurredAt, DateTimeOffset CreatedAt, IReadOnlyList<ParticipantDetails> Participants);
public sealed record ParticipantDetails(Guid Id, string DisplayName);
public sealed record InteractionSummary(Guid Id, string Title, string Description, string? UserThoughts,
    DateTimeOffset OccurredAt, DateTimeOffset CreatedAt);
