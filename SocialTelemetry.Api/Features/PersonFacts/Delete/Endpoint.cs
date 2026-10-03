using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.PersonFacts.Delete;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/people/{personId}/facts/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var fact = await dbContext.PersonFacts
            .SingleOrDefaultAsync(
                fact => fact.Id == request.Id && fact.PersonId == request.PersonId,
                cancellationToken);

        if (fact is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        dbContext.PersonFacts.Remove(fact);
        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(fact.Id), cancellationToken);
    }
}
