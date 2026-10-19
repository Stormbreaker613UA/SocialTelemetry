using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.Create;

namespace SocialTelemetry.Api.Features.Interactions.Create;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/interactions");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var interaction = await scenario.ExecuteAsync(new Input
            {
                UserProfileId = request.UserProfileId,
                Title = request.Title,
                Description = request.Description,
                UserThoughts = request.UserThoughts,
                OccurredAt = request.OccurredAt,
                ParticipantIds = request.ParticipantIds
            }, cancellationToken);
            await Send.ResponseAsync(new Response(interaction.Id), 201, cancellationToken);
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
