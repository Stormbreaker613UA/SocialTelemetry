using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.Update;

public sealed class Endpoint(AppDbContext dbContext) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/people/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            AddError("DisplayName is required.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }

        var person = await dbContext.People
            .SingleOrDefaultAsync(person => person.Id == request.Id, cancellationToken);

        if (person is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        person.DisplayName = request.DisplayName.Trim();
        person.Age = request.Age;
        person.Gender = request.Gender;
        person.Description = request.Description;
        person.RelationshipContext = request.RelationshipContext;
        person.HowWeMet = request.HowWeMet;
        person.Notes = request.Notes;
        person.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(
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
            person.UpdatedAt), cancellationToken);
    }
}
