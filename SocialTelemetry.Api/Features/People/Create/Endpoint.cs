using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.Create;

namespace SocialTelemetry.Api.Features.People.Create;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/people");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var person = await scenario.ExecuteAsync(new Input
            {
                UserProfileId = request.UserProfileId,
                DisplayName = request.DisplayName,
                Age = request.Age,
                Gender = request.Gender,
                Description = request.Description,
                RelationshipContext = request.RelationshipContext,
                HowWeMet = request.HowWeMet,
                Notes = request.Notes
            }, cancellationToken);
            await Send.ResponseAsync(new Response(person.Id), 201, cancellationToken);
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
