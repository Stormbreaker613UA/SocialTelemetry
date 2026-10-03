using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.Interactions.Delete;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/interactions/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var interaction = await dbContext.Interactions
            .SingleOrDefaultAsync(interaction => interaction.Id == request.Id, cancellationToken);

        if (interaction is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        dbContext.Interactions.Remove(interaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(interaction.Id), cancellationToken);
    }
}
