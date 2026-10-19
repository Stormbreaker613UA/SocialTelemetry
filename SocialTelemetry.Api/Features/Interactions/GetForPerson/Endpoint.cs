using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.GetForPerson;

namespace SocialTelemetry.Api.Features.Interactions.GetForPerson;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people/{personId}/interactions");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var interactions = await scenario.ExecuteAsync(request.PersonId, cancellationToken);
            await Send.OkAsync(new Response(interactions.Select(interaction => new InteractionResponse(interaction.Id,
                interaction.Title, interaction.Description, interaction.UserThoughts, interaction.OccurredAt, interaction.CreatedAt)).ToList()), cancellationToken);
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
