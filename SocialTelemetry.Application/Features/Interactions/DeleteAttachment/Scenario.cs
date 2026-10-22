using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.DeleteAttachment;

public sealed class Scenario(IAttachmentPersistence persistence, LocalProfileOperation operation)
{
    public async Task<Guid> ExecuteAsync(Guid interactionId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        var attachment = await persistence.FindAttachmentAsync(ownerId, interactionId, attachmentId, false, cancellationToken)
            ?? throw new NotFoundException();
        attachment.Status = AttachmentStatus.Deleting;
        await persistence.SaveAttachmentStatusAsync(attachment, cancellationToken);
        if (attachment.StorageKey is not null)
            await persistence.DeleteMarkedFileAsync(attachment.StorageKey, cancellationToken);
        await persistence.DeleteAttachmentAsync(attachment, cancellationToken);
        return attachment.Id;
    }
}
