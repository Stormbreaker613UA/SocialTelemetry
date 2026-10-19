using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.GetById;

namespace SocialTelemetry.Api.Features.People.GetById;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var person = await scenario.ExecuteAsync(request.Id, cancellationToken);
            await Send.OkAsync(new Response(person.Id, person.UserProfileId, person.DisplayName, person.Age,
                person.Gender, person.Description, person.RelationshipContext, person.HowWeMet,
                person.Notes, person.CreatedAt, person.UpdatedAt, person.ArchivedAt), cancellationToken);
        }
        catch (NotFoundException)
        {
            await Send.NotFoundAsync(cancellationToken);
        }
    }
}
