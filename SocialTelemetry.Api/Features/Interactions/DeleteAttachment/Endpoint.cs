using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.Interactions.DeleteAttachment;

public sealed class Endpoint(AppDbContext dbContext, LocalProfileOperation operation, IAttachmentStorage attachmentStorage) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/interactions/{interactionId}/attachments/{attachmentId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var attachment = await dbContext.InteractionAttachments.Where(attachment => attachment.Interaction.UserProfileId == operation.RequireOwner())
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

        attachment.Status = AttachmentStatus.Deleting;
        await dbContext.SaveChangesAsync(cancellationToken);

        if (attachment.StorageKey is not null)
        {
            await AttachmentFileCleanup.DeleteForMarkedAttachmentsAsync(
                dbContext, attachmentStorage, attachment.StorageKey, cancellationToken);
        }

        dbContext.InteractionAttachments.Remove(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(attachment.Id), cancellationToken);
    }
}
