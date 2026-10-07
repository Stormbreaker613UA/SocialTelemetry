using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.GetAll;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var people = await dbContext.People
            .AsNoTracking()
            .Where(person => person.UserProfileId == request.UserProfileId)
            .Where(person => request.Archived == null || (person.ArchivedAt != null) == request.Archived)
            .OrderBy(person => person.DisplayName)
            .Select(person => new PersonResponse(
                person.Id,
                person.DisplayName,
                person.Age,
                person.Gender,
                person.Description,
                person.RelationshipContext,
                person.HowWeMet,
                person.Notes,
                person.CreatedAt,
                person.UpdatedAt,
                person.ArchivedAt))
            .ToListAsync(cancellationToken);

        await Send.OkAsync(new Response(people), cancellationToken);
    }
}
