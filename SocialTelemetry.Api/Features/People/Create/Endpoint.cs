using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.Create;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/people");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (request.UserProfileId == Guid.Empty || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            AddError("UserProfileId and DisplayName are required.");
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

        var now = DateTimeOffset.UtcNow;
        var person = new Person
        {
            Id = Guid.NewGuid(),
            UserProfileId = request.UserProfileId,
            DisplayName = request.DisplayName.Trim(),
            Age = request.Age,
            Gender = request.Gender,
            Description = request.Description,
            RelationshipContext = request.RelationshipContext,
            HowWeMet = request.HowWeMet,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.People.Add(person);
        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.ResponseAsync(new Response(person.Id), 201, cancellationToken);
    }
}
