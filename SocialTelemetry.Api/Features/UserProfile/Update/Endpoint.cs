using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.UserProfile.Update;

namespace SocialTelemetry.Api.Features.UserProfile.Update;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/user-profile");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var userProfile = await scenario.ExecuteAsync(new Input
            {
                DisplayName = request.DisplayName,
                AboutMe = request.AboutMe,
                CommunicationStyle = request.CommunicationStyle,
                Goals = request.Goals,
                Preferences = request.Preferences,
                Boundaries = request.Boundaries,
                AiInstructions = request.AiInstructions
            }, cancellationToken);
            await Send.OkAsync(new Response(
                userProfile.Id,
                userProfile.DisplayName,
                userProfile.AboutMe,
                userProfile.CommunicationStyle,
                userProfile.Goals,
                userProfile.Preferences,
                userProfile.Boundaries,
                userProfile.AiInstructions), cancellationToken);
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
