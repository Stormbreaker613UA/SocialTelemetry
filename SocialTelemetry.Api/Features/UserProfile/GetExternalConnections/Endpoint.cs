using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.UserProfile.GetExternalConnections;
using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.UserProfile.GetExternalConnections;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<IReadOnlyList<ExternalConnectionResponse>>
{
    public override void Configure()
    {
        Get("/user-profile/external-connections");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connections = await scenario.ExecuteAsync(cancellationToken);
            var response = connections.Select(connection => new ExternalConnectionResponse(connection.Id, connection.UserProfileId,
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
