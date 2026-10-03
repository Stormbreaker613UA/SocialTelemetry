using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.Interactions.Update;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/interactions/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var participantIds = request.ParticipantIds?.Distinct().ToList() ?? [];

        if (string.IsNullOrWhiteSpace(request.Title) ||
            string.IsNullOrWhiteSpace(request.Description) ||
            request.OccurredAt == default ||
            participantIds.Count == 0)
        {
            AddError("Title, Description, OccurredAt, and at least one participant are required.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }

        var interaction = await dbContext.Interactions
            .Include(interaction => interaction.Participants)
            .SingleOrDefaultAsync(interaction => interaction.Id == request.Id, cancellationToken);

        if (interaction is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var existingParticipantCount = await dbContext.People
            .AsNoTracking()
            .CountAsync(
                person => person.UserProfileId == interaction.UserProfileId && participantIds.Contains(person.Id),
                cancellationToken);

        if (existingParticipantCount != participantIds.Count)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        interaction.Title = request.Title.Trim();
        interaction.Description = request.Description.Trim();
        interaction.UserThoughts = request.UserThoughts;
        interaction.OccurredAt = request.OccurredAt.ToUniversalTime();

        var removedParticipants = interaction.Participants
            .Where(participant => !participantIds.Contains(participant.PersonId))
            .ToList();
        dbContext.InteractionParticipants.RemoveRange(removedParticipants);

        var existingParticipantIds = interaction.Participants
            .Select(participant => participant.PersonId)
            .ToHashSet();
        var addedParticipants = participantIds
            .Where(personId => !existingParticipantIds.Contains(personId))
            .Select(personId => new InteractionParticipant
            {
                InteractionId = interaction.Id,
                PersonId = personId
            })
            .ToList();
        dbContext.InteractionParticipants.AddRange(addedParticipants);

        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(interaction.Id), cancellationToken);
    }
}
