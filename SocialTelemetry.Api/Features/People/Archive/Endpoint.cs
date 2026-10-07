using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.Archive;

public sealed class Endpoint(AppDbContext database) : Endpoint<Request>
{
    public override void Configure()
    {
        Put("/people/{id}/archive");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var person = await database.People.SingleOrDefaultAsync(person => person.Id == request.Id, cancellationToken);
        if (person is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }
        person.ArchivedAt ??= DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
