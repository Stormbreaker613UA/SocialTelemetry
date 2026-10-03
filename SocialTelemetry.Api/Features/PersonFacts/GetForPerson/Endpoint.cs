using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.PersonFacts.GetForPerson;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people/{personId}/facts");
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

        var facts = await dbContext.PersonFacts
            .AsNoTracking()
            .Where(fact => fact.PersonId == request.PersonId)
            .OrderBy(fact => fact.CreatedAt)
            .Select(fact => new PersonFactResponse(fact.Id, fact.Value, fact.Source, fact.CreatedAt))
            .ToListAsync(cancellationToken);

        await Send.OkAsync(new Response(facts), cancellationToken);
    }
}
