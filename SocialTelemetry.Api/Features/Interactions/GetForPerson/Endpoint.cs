using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.Interactions.GetForPerson;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people/{personId}/interactions");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var personExists = await dbContext.People
            .AsNoTracking()
            .AnyAsync(person => person.Id == request.PersonId, cancellationToken);

        if (!personExists)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var interactions = await dbContext.Interactions
            .AsNoTracking()
            .Where(interaction => interaction.Participants.Any(participant => participant.PersonId == request.PersonId))
            .OrderByDescending(interaction => interaction.OccurredAt)
            .Select(interaction => new InteractionResponse(
                interaction.Id,
                interaction.Title,
                interaction.Description,
                interaction.UserThoughts,
                interaction.OccurredAt,
                interaction.CreatedAt))
            .ToListAsync(cancellationToken);

        await Send.OkAsync(new Response(interactions), cancellationToken);
    }
}
