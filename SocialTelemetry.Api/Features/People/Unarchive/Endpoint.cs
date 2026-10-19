using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.Unarchive;

public sealed class Endpoint(AppDbContext database, LocalProfileOperation operation) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/people/{id}/archive");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var person = await database.People.Where(person => person.UserProfileId == operation.RequireOwner()).SingleOrDefaultAsync(person => person.Id == request.Id, cancellationToken);
        if (person is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }
        person.ArchivedAt = null;
        await database.SaveChangesAsync(cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
