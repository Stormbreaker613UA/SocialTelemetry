using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.GetById;

public sealed class Endpoint(AppDbContext dbContext, LocalProfileOperation operation) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var person = await dbContext.People.Where(person => person.UserProfileId == operation.RequireOwner())
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
                person.UpdatedAt,
                person.ArchivedAt))
            .SingleOrDefaultAsync(cancellationToken);

        if (person is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        await Send.OkAsync(person, cancellationToken);
    }
}
