using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.GetAll;

namespace SocialTelemetry.Api.Features.People.GetAll;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var people = await scenario.ExecuteAsync(request.UserProfileId, request.Archived, cancellationToken);
        await Send.OkAsync(new Response(people.Select(person => new PersonResponse(
            person.Id, person.DisplayName, person.Age, person.Gender, person.Description,
            person.RelationshipContext, person.HowWeMet, person.Notes, person.CreatedAt,
            person.UpdatedAt, person.ArchivedAt)).ToList()), cancellationToken);
    }
}
