using SocialTelemetry.Api.Common.Profiles;
using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.Update;

namespace SocialTelemetry.Api.Features.Interactions.Update;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/interactions/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var interaction = await scenario.ExecuteAsync(new Input
            {
                Id = request.Id,
                Title = request.Title,
                Description = request.Description,
                UserThoughts = request.UserThoughts,
                OccurredAt = request.OccurredAt,
                ParticipantIds = request.ParticipantIds
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
