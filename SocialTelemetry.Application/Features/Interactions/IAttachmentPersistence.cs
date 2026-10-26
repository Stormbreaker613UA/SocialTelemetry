using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions;

public interface IAttachmentPersistence
{
    Task<bool> InteractionExistsAsync(Guid ownerId, Guid interactionId, CancellationToken cancellationToken);
    Task<InteractionAttachment?> FindAttachmentAsync(Guid ownerId, Guid interactionId, Guid attachmentId, bool readOnly, CancellationToken cancellationToken);
    Task AddPendingAttachmentAsync(InteractionAttachment attachment, CancellationToken cancellationToken);
    Task CompletePendingAttachmentAsync(Guid ownerId, InteractionAttachment attachment, CancellationToken cancellationToken);
    Task SaveAttachmentStatusAsync(InteractionAttachment attachment, CancellationToken cancellationToken);
    Task DeleteMarkedFileAsync(string key, CancellationToken cancellationToken);
    Task DeleteAttachmentAsync(InteractionAttachment attachment, CancellationToken cancellationToken);
    Task CleanupFailedUploadAsync(Guid ownerId, InteractionAttachment attachment);
}
