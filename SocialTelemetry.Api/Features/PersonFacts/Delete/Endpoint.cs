using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.PersonFacts.Delete;

namespace SocialTelemetry.Api.Features.PersonFacts.Delete;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/people/{personId}/facts/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var fact = await scenario.ExecuteAsync(new Input
            {
                PersonId = request.PersonId,
                Id = request.Id
            }, cancellationToken);
            await Send.OkAsync(new Response(fact.Id), cancellationToken);
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
