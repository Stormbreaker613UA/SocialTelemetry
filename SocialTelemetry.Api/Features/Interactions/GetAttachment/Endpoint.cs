using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.Interactions.GetAttachment;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/interactions/{interactionId}/attachments/{attachmentId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var attachment = await dbContext.InteractionAttachments
            .AsNoTracking()
            .Where(attachment =>
                attachment.Id == request.AttachmentId &&
                attachment.InteractionId == request.InteractionId)
            .Select(attachment => new Response(
                attachment.Id,
                attachment.InteractionId,
                attachment.Type,
                attachment.Status,
                attachment.MimeType,
                attachment.Type == AttachmentType.Text ? attachment.TextContent : null,
                attachment.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);

        if (attachment is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        await Send.OkAsync(attachment, cancellationToken);
    }
}
