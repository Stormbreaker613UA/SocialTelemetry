using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.UpdateExternalConnection;
using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.People.UpdateExternalConnection;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, ExternalConnectionResponse>
{
    public override void Configure()
    {
        Put("/people/{personId}/external-connections/{connectionId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await scenario.ExecuteAsync(new Input
            {
                PersonId = request.PersonId,
                ConnectionId = request.ConnectionId,
                Platform = request.Platform,
                ExternalUserId = request.ExternalUserId,
                Handle = request.Handle,
                DisplayName = request.DisplayName,
                ProfileUrl = request.ProfileUrl
            }, cancellationToken);
            var response = new ExternalConnectionResponse(connection.Id, connection.PersonId,
                connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
                connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt);
            await Send.OkAsync(response, cancellationToken);
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
