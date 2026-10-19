using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileConnections;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.UserProfile.UpdateExternalConnection;

public sealed class Scenario(IProfileConnectionsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<UserProfileExternalConnection> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        var parentId = ownerId;
        if (!await persistence.ProfileExistsAsync(ownerId, parentId, cancellationToken)) throw new NotFoundException();

        if (!request.TryNormalize(out var input))
        {
            throw new FeatureValidationException("Invalid external connection fields or profile URL.");
        }
        var connection = await persistence.FindProfileConnectionAsync(ownerId, parentId, request.ConnectionId, false, cancellationToken);
        if (connection is null)
        {
            throw new NotFoundException();
        }
        connection.Platform = input.Platform;
        connection.ExternalUserId = input.ExternalUserId;
        connection.Handle = input.Handle;
        connection.DisplayName = input.DisplayName;
        connection.ProfileUrl = input.ProfileUrl;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await persistence.SaveProfileConnectionAsync(connection, cancellationToken);
        return connection;
    }
}
