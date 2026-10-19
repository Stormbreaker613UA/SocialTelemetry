using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.Delete;

public sealed class Endpoint(AppDbContext dbContext, LocalProfileOperation operation) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/people/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var person = await dbContext.People.Where(person => person.UserProfileId == operation.RequireOwner())
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
