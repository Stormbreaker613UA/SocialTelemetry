using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.Interactions.DeleteAttachment;

public sealed class Endpoint(AppDbContext dbContext, IAttachmentStorage attachmentStorage) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/interactions/{interactionId}/attachments/{attachmentId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var attachment = await dbContext.InteractionAttachments
            .SingleOrDefaultAsync(
                attachment =>
                    attachment.Id == request.AttachmentId &&
                    attachment.InteractionId == request.InteractionId,
                cancellationToken);

        if (attachment is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        dbContext.InteractionAttachments.Remove(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (attachment.StorageKey is not null)
        {
            await attachmentStorage.DeleteAsync(attachment.StorageKey, cancellationToken);
        }

        await Send.OkAsync(new Response(attachment.Id), cancellationToken);
    }
}
