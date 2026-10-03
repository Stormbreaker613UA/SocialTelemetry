using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.PersonFacts.Update;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/people/{personId}/facts/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            AddError("Value is required.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }

        var fact = await dbContext.PersonFacts
            .SingleOrDefaultAsync(
                fact => fact.Id == request.Id && fact.PersonId == request.PersonId,
                cancellationToken);

        if (fact is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        fact.Value = request.Value.Trim();
        fact.Source = request.Source;

        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(fact.Id, fact.PersonId, fact.Value, fact.Source, fact.CreatedAt), cancellationToken);
    }
}
