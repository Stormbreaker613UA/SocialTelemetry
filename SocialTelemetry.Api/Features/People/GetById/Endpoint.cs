using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.GetById;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var person = await dbContext.People
            .AsNoTracking()
            .Where(person => person.Id == request.Id)
            .Select(person => new Response(
                person.Id,
                person.UserProfileId,
                person.DisplayName,
                person.Age,
                person.Gender,
                person.Description,
                person.RelationshipContext,
                person.HowWeMet,
                person.Notes,
                person.CreatedAt,
                person.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);

        if (person is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        await Send.OkAsync(person, cancellationToken);
    }
}
