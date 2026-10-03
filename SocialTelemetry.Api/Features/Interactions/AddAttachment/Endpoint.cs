using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.Interactions.AddAttachment;

public sealed class Endpoint(AppDbContext dbContext, IAttachmentStorage attachmentStorage) : Endpoint<Request, Response>
{
    private const long MaximumFileSizeBytes = 10 * 1024 * 1024;

    public override void Configure()
    {
        Post("/interactions/{interactionId}/attachments");
        AllowAnonymous();
        AllowFileUploads();
        MaxRequestBodySize(MaximumFileSizeBytes + 1024 * 1024);
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Type))
        {
            await SendBadRequestAsync("Attachment type is not supported.", cancellationToken);
            return;
        }

        if (request.Type == AttachmentType.Text)
        {
            if (request.File is not null || string.IsNullOrWhiteSpace(request.TextContent))
            {
                await SendBadRequestAsync("Text attachments require TextContent and cannot include a file.", cancellationToken);
                return;
            }
        }
        else if (request.File is null || request.File.Length == 0 || request.TextContent is not null)
        {
            await SendBadRequestAsync("File attachments require a non-empty file and cannot include TextContent.", cancellationToken);
            return;
        }

        if (request.File?.Length > MaximumFileSizeBytes)
        {
            await SendBadRequestAsync("Attachment files cannot exceed 10 MB.", cancellationToken);
            return;
        }

        var interactionExists = await dbContext.Interactions
            .AsNoTracking()
            .AnyAsync(interaction => interaction.Id == request.InteractionId, cancellationToken);

        if (!interactionExists)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var attachment = new InteractionAttachment
        {
            Id = Guid.NewGuid(),
            InteractionId = request.InteractionId,
            Type = request.Type,
            TextContent = request.Type == AttachmentType.Text ? request.TextContent?.Trim() : null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (request.File is not null)
        {
            await using var content = request.File.OpenReadStream();
            attachment.StorageKey = await attachmentStorage.SaveAsync(
                content,
                request.File.FileName,
                request.File.ContentType,
                cancellationToken);
            attachment.MimeType = request.File.ContentType;
        }

        try
        {
            dbContext.InteractionAttachments.Add(attachment);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (attachment.StorageKey is not null)
            {
                // Request cancellation must not prevent cleanup of an unreferenced file.
                await attachmentStorage.DeleteAsync(attachment.StorageKey, CancellationToken.None);
            }

            throw;
        }

        await Send.ResponseAsync(
            new Response(
                attachment.Id,
                attachment.InteractionId,
                attachment.Type,
                attachment.TextContent,
                attachment.StorageKey,
                attachment.MimeType,
                attachment.CreatedAt),
            201,
            cancellationToken);
    }

    private async Task SendBadRequestAsync(string message, CancellationToken cancellationToken)
    {
        AddError(message);
        await Send.ErrorsAsync(400, cancellationToken);
    }
}
