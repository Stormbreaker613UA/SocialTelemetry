using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.GetById;

namespace SocialTelemetry.Api.Features.Interactions.GetById;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/interactions/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var interaction = await scenario.ExecuteAsync(request.Id, cancellationToken);
            await Send.OkAsync(new Response(interaction.Id, interaction.UserProfileId, interaction.Title, interaction.Description,
                interaction.UserThoughts, interaction.OccurredAt, interaction.CreatedAt,
                interaction.Participants.Select(participant => new ParticipantResponse(participant.Id, participant.DisplayName)).ToList()), cancellationToken);
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
