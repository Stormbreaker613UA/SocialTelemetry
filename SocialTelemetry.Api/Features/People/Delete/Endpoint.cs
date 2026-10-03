using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.Delete;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/people/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var person = await dbContext.People
            .SingleOrDefaultAsync(person => person.Id == request.Id, cancellationToken);

        if (person is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        dbContext.People.Remove(person);
        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(person.Id), cancellationToken);
    }
}
