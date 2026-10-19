using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.UserProfile.Get;

namespace SocialTelemetry.Api.Features.UserProfile.Get;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<Response>
{
    public override void Configure()
    {
        Get("/user-profile");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var userProfile = await scenario.ExecuteAsync(cancellationToken);
            await Send.OkAsync(new Response(userProfile.Id, userProfile.DisplayName, userProfile.AboutMe,
                userProfile.CommunicationStyle, userProfile.Goals, userProfile.Preferences,
                userProfile.Boundaries, userProfile.AiInstructions), cancellationToken);
        }
        catch (NotFoundException)
        {
            await Send.NotFoundAsync(cancellationToken);
        }
    }
}
