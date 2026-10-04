using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.Interactions.Delete;

public sealed class Endpoint(AppDbContext dbContext, IAttachmentStorage attachmentStorage) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/interactions/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var interaction = await dbContext.Interactions
            .Include(interaction => interaction.Attachments)
            .SingleOrDefaultAsync(interaction => interaction.Id == request.Id, cancellationToken);

        if (interaction is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        foreach (var attachment in interaction.Attachments)
        {
            attachment.Status = AttachmentStatus.Deleting;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var attachment in interaction.Attachments)
        {
            if (attachment.StorageKey is not null)
            {
                await attachmentStorage.DeleteAsync(attachment.StorageKey, cancellationToken);
                await attachmentStorage.DeleteStagedAsync(attachment.StorageKey, cancellationToken);
            }
        }

        dbContext.Interactions.Remove(interaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(interaction.Id), cancellationToken);
    }
}
