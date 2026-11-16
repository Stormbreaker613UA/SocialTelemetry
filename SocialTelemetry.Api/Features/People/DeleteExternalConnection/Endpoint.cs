using SocialTelemetry.Api.Common.Profiles;
using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.DeleteExternalConnection;
using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.People.DeleteExternalConnection;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/people/{personId}/external-connections/{connectionId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            await scenario.ExecuteAsync(new Input
            {
                PersonId = request.PersonId,
                ConnectionId = request.ConnectionId
            }, cancellationToken);
            var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
            await Send.NoContentAsync(transport);
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
