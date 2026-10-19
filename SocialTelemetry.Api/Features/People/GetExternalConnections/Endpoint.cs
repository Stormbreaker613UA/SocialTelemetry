using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.GetExternalConnections;
using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.People.GetExternalConnections;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, IReadOnlyList<ExternalConnectionResponse>>
{
    public override void Configure()
    {
        Get("/people/{personId}/external-connections");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var connections = await scenario.ExecuteAsync(new Input
            {
                PersonId = request.PersonId
            }, cancellationToken);
            var response = connections.Select(connection => new ExternalConnectionResponse(connection.Id, connection.PersonId,
                connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
                connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt)).ToList();
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
