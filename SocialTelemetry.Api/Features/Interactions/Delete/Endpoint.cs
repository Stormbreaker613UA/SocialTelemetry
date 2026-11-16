using SocialTelemetry.Api.Common.Profiles;
using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.Delete;

namespace SocialTelemetry.Api.Features.Interactions.Delete;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/interactions/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var interaction = await scenario.ExecuteAsync(new Input
            {
                Id = request.Id
            }, cancellationToken);
            var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
            await Send.OkAsync(new Response(interaction.Id), transport);
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
