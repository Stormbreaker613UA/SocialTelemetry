using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions;

public interface ITranscriptPersistence
{
    Task<TranscriptSource> LoadSourceAsync(Guid ownerId, Guid interactionId, Guid attachmentId, CancellationToken cancellationToken);
    Task<AttachmentTranscript?> ReadTranscriptAsync(Guid ownerId, Guid attachmentId, CancellationToken cancellationToken);
    Task<AttachmentTranscript?> ReadReadyTranscriptAsync(Guid ownerId, Guid interactionId, Guid attachmentId, CancellationToken cancellationToken);

    // The decision is synchronous and only inspects/mutates these owned values.
    // File reads, hashing and inference must finish before entering this operation.
    Task<TranscriptMutation> CommitTranscriptAsync(Guid ownerId, Guid interactionId, Guid attachmentId,
        Action<TranscriptSource> validateSource, Func<AttachmentTranscript?, TranscriptMutation> decide,
        CancellationToken cancellationToken);
}

public sealed record TranscriptSource(Guid ProfileId, AttachmentType Type, AttachmentStatus Status, string? StorageKey);
public sealed record TranscriptMutation(AttachmentTranscript Transcript, bool SaveChanges, bool Created, bool Reused);
