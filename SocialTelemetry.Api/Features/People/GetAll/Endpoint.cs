using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Profiles;
using SocialTelemetry.Api.Common.Exceptions;

namespace SocialTelemetry.Api.Features.People.GetAll;

public sealed class Endpoint(AppDbContext dbContext, LocalProfileOperation operation) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        if (request.UserProfileId != Guid.Empty && request.UserProfileId != owner) throw new NotFoundException();
        var people = await dbContext.People.Where(person => person.UserProfileId == operation.RequireOwner())
            .AsNoTracking()
            .Where(person => person.UserProfileId == owner)
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
