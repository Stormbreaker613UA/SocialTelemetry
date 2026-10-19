using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.Interactions.DownloadAttachment;

public sealed class Endpoint(AppDbContext dbContext, LocalProfileOperation operation, IAttachmentStorage attachmentStorage) : Endpoint<Request>
{
    public override void Configure()
    {
        Get("/interactions/{interactionId}/attachments/{attachmentId}/content");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var attachment = await dbContext.InteractionAttachments.Where(attachment => attachment.Interaction.UserProfileId == operation.RequireOwner())
            .AsNoTracking()
            .Where(attachment =>
                attachment.Id == request.AttachmentId &&
                attachment.InteractionId == request.InteractionId)
            .Select(attachment => new
            {
                attachment.Type,
                attachment.Status,
                attachment.StorageKey,
                attachment.MimeType
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (attachment is null || attachment.Status != AttachmentStatus.Ready)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        if (attachment.Type == AttachmentType.Text)
        {
            AddError("Text attachments do not have file content.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }

        if (attachment.StorageKey is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        await using var content = await attachmentStorage.OpenReadAsync(attachment.StorageKey, cancellationToken);

        if (content is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        await Send.StreamAsync(
            content,
            fileName: attachment.StorageKey,
            fileLengthBytes: content.Length,
            contentType: attachment.MimeType ?? "application/octet-stream",
            cancellation: cancellationToken);
    }
}
