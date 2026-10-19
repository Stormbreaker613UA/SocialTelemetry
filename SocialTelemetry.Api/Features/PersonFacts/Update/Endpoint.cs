using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.PersonFacts.Update;

namespace SocialTelemetry.Api.Features.PersonFacts.Update;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/people/{personId}/facts/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var fact = await scenario.ExecuteAsync(new Input
            {
                PersonId = request.PersonId,
                Id = request.Id,
                Value = request.Value,
                Source = request.Source
            }, cancellationToken);
            await Send.OkAsync(new Response(fact.Id, fact.PersonId, fact.Value, fact.Source, fact.CreatedAt), cancellationToken);
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
