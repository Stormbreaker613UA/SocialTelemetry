using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.Interactions.GetById;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/interactions/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var interaction = await dbContext.Interactions
            .AsNoTracking()
            .Where(interaction => interaction.Id == request.Id)
            .Select(interaction => new Response(
                interaction.Id,
                interaction.UserProfileId,
                interaction.Title,
                interaction.Description,
                interaction.UserThoughts,
                interaction.OccurredAt,
                interaction.CreatedAt,
                interaction.Participants
                    .OrderBy(participant => participant.Person.DisplayName)
                    .Select(participant => new ParticipantResponse(participant.PersonId, participant.Person.DisplayName))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);

        if (interaction is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        await Send.OkAsync(interaction, cancellationToken);
    }
}
