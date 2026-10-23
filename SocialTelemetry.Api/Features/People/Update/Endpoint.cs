using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.Update;

namespace SocialTelemetry.Api.Features.People.Update;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/people/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var person = await scenario.ExecuteAsync(new Input
            {
                Id = request.Id,
                DisplayName = request.DisplayName,
                Age = request.Age,
                Gender = request.Gender,
                Description = request.Description,
                RelationshipContext = request.RelationshipContext,
                HowWeMet = request.HowWeMet,
                Notes = request.Notes
            }, cancellationToken);
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
                person.UpdatedAt, person.IsFavorite), cancellationToken);
        }
        catch (FeatureValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(400, cancellationToken);
        }
        catch (NotFoundException)
        {
            await Send.NotFoundAsync(cancellationToken);
        }
    }
}
