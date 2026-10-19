using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.Interactions.GetById;

public sealed class Endpoint(AppDbContext dbContext, LocalProfileOperation operation) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/interactions/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var interaction = await dbContext.Interactions.Where(interaction => interaction.UserProfileId == operation.RequireOwner())
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
                    .Where(participant => participant.Person.UserProfileId == operation.RequireOwner())
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
