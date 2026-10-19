using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileConnections;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.UserProfile.GetExternalConnection;

public sealed class Scenario(IProfileConnectionsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<UserProfileExternalConnection> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        var parentId = ownerId;
        if (!await persistence.ProfileExistsAsync(ownerId, parentId, cancellationToken)) throw new NotFoundException();

        var connection = await persistence.FindProfileConnectionAsync(ownerId, parentId, request.ConnectionId, true, cancellationToken);
        if (connection is null)
        {
            throw new NotFoundException();
        }

        return connection;
    }
}
