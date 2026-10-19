using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.UserProfile.DeleteExternalConnection;
using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.UserProfile.DeleteExternalConnection;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/user-profile/external-connections/{connectionId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            await scenario.ExecuteAsync(new Input
            {
                ConnectionId = request.ConnectionId
            }, cancellationToken);
            await Send.NoContentAsync(cancellationToken);
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
