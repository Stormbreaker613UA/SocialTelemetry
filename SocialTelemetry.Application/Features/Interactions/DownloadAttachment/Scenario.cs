using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.DownloadAttachment;

public sealed class Scenario(IAttachmentPersistence persistence, LocalProfileOperation operation, IAttachmentStorage storage)
{
    public async Task<Result> ExecuteAsync(Guid interactionId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        var attachment = await persistence.FindAttachmentAsync(ownerId, interactionId, attachmentId, true, cancellationToken);
        if (attachment is null || attachment.Status != AttachmentStatus.Ready) throw new NotFoundException();
        if (attachment.Type == AttachmentType.Text)
            throw new FeatureValidationException("Text attachments do not have file content.");
        if (attachment.StorageKey is null) throw new NotFoundException();
        var content = await storage.OpenReadAsync(attachment.StorageKey, cancellationToken) ?? throw new NotFoundException();
        return new Result(content, attachment.StorageKey, attachment.MimeType ?? "application/octet-stream");
    }
}

public sealed record Result(Stream Content, string FileName, string MimeType);
