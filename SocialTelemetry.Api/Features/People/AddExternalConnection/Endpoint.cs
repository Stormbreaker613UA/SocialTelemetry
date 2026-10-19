using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.AddExternalConnection;
using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.People.AddExternalConnection;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, ExternalConnectionResponse>
{
    public override void Configure()
    {
        Post("/people/{personId}/external-connections");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await scenario.ExecuteAsync(new Input
            {
                PersonId = request.PersonId,
                Platform = request.Platform,
                ExternalUserId = request.ExternalUserId,
                Handle = request.Handle,
                DisplayName = request.DisplayName,
                ProfileUrl = request.ProfileUrl
            }, cancellationToken);
            var response = new ExternalConnectionResponse(connection.Id, connection.PersonId,
                connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
                connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt);
            await Send.ResponseAsync(response, 201, cancellationToken);
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
