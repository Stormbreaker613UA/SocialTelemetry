using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.Interactions.Create;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/interactions");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var participantIds = request.ParticipantIds?.Distinct().ToList() ?? [];

        if (request.UserProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Title) ||
            string.IsNullOrWhiteSpace(request.Description) ||
            request.OccurredAt == default ||
            participantIds.Count == 0)
        {
            AddError("UserProfileId, Title, Description, OccurredAt, and at least one participant are required.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }

        var userProfileExists = await dbContext.UserProfiles
            .AsNoTracking()
            .AnyAsync(userProfile => userProfile.Id == request.UserProfileId, cancellationToken);

        if (!userProfileExists)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var existingParticipantCount = await dbContext.People
            .AsNoTracking()
            .CountAsync(
                person => person.UserProfileId == request.UserProfileId && participantIds.Contains(person.Id),
                cancellationToken);

        if (existingParticipantCount != participantIds.Count)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var interaction = new Interaction
        {
            Id = Guid.NewGuid(),
            UserProfileId = request.UserProfileId,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            UserThoughts = request.UserThoughts,
            OccurredAt = request.OccurredAt.ToUniversalTime(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        interaction.Participants = participantIds
            .Select(personId => new InteractionParticipant
            {
                InteractionId = interaction.Id,
                PersonId = personId
            })
            .ToList();

        dbContext.Interactions.Add(interaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.ResponseAsync(new Response(interaction.Id), 201, cancellationToken);
    }
}
