using SocialTelemetry.Api.Common.Profiles;
using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.PersonFacts.Add;

namespace SocialTelemetry.Api.Features.PersonFacts.Add;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/people/{personId}/facts");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var fact = await scenario.ExecuteAsync(new Input
            {
                PersonId = request.PersonId,
                Value = request.Value,
                Source = request.Source
            }, cancellationToken);
            var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
            await Send.ResponseAsync(new Response(fact.Id, fact.PersonId, fact.Value, fact.Source, fact.CreatedAt), 201, transport);
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
