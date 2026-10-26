using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.AddAttachment;

public sealed class Scenario(IAttachmentPersistence persistence, LocalProfileOperation operation,
    IAttachmentStorage storage, UploadOptions options)
{
    public async Task<InteractionAttachment> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Lifetime);
        cancellationToken = lifetime.Token;
        if (!Enum.IsDefined(request.Type))
            throw new FeatureValidationException("Attachment type is not supported.");
        if (request.Type == AttachmentType.Text)
        {
            if (request.File is not null || string.IsNullOrWhiteSpace(request.TextContent))
                throw new FeatureValidationException("Text attachments require TextContent and cannot include a file.");
        }
        else if (request.File is null || request.File.Length == 0 || request.TextContent is not null)
            throw new FeatureValidationException("File attachments require a non-empty file and cannot include TextContent.");
        if (request.File?.Length > options.AttachmentMaxBytes)
            throw new FeatureValidationException("Attachment files exceed the configured upload limit.");

        if (!await persistence.InteractionExistsAsync(ownerId, request.InteractionId, cancellationToken))
            throw new NotFoundException();
        var attachment = new InteractionAttachment
        {
            Id = Guid.NewGuid(),
            InteractionId = request.InteractionId,
            Type = request.Type,
            Status = request.Type == AttachmentType.Text ? AttachmentStatus.Ready : AttachmentStatus.Pending,
            TextContent = request.Type == AttachmentType.Text ? request.TextContent?.Trim() : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (request.File is { } file)
        {
            await using var content = file.OpenRead();
            using var buffer = await ReadBoundedAsync(content, cancellationToken);
            attachment.StorageKey = await storage.SaveAsync(buffer, file.FileName, file.MimeType, cancellationToken);
            attachment.MimeType = file.MimeType;
        }
        try
        {
            // A Pending file row is not a completed user mutation. Only the Ready commit
            // extends activity; failed completion/cleanup must not keep access alive.
            if (attachment.Status == AttachmentStatus.Ready)
            {
                using var successfulMutation = await operation.EnterAsync(cancellationToken, userMutation: true);
                await persistence.AddPendingAttachmentAsync(attachment, successfulMutation.CancellationToken);
            }
            else await persistence.AddPendingAttachmentAsync(attachment, cancellationToken);
            if (attachment.StorageKey is not null)
            {
                if (!await storage.CompleteUploadAsync(attachment.StorageKey, cancellationToken))
                    throw new IOException("The staged attachment file is missing.");
                attachment.Status = AttachmentStatus.Ready;
                using var successfulMutation = await operation.EnterAsync(cancellationToken, userMutation: true);
                await persistence.CompletePendingAttachmentAsync(ownerId, attachment, successfulMutation.CancellationToken);
            }
        }
        catch
        {
            await persistence.CleanupFailedUploadAsync(ownerId, attachment);
            throw;
        }
        return attachment;
    }

    private async Task<MemoryStream> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        try
        {
            var chunk = new byte[81920];
            int count;
            while ((count = await content.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
            {
                if (buffer.Length + count > options.AttachmentMaxBytes)
                    throw new FeatureValidationException("Attachment files exceed the configured upload limit.");
                await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            }
            if (buffer.Length == 0)
                throw new FeatureValidationException("File attachments require a non-empty file and cannot include TextContent.");
            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
