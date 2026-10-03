using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.PersonFacts.Add;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/people/{personId}/facts");
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

        var personExists = await dbContext.People
            .AsNoTracking()
            .AnyAsync(person => person.Id == request.PersonId, cancellationToken);

        if (!personExists)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var fact = new PersonFact
        {
            Id = Guid.NewGuid(),
            PersonId = request.PersonId,
            Value = request.Value.Trim(),
            Source = request.Source,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.PersonFacts.Add(fact);
        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.ResponseAsync(new Response(fact.Id, fact.PersonId, fact.Value, fact.Source, fact.CreatedAt), 201, cancellationToken);
    }
}
