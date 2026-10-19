using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileConnections;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.UserProfile.DeleteExternalConnection;

public sealed class Scenario(IProfileConnectionsPersistence persistence, LocalProfileOperation operation)
{
    public async Task ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        var parentId = ownerId;
        if (!await persistence.ProfileExistsAsync(ownerId, parentId, cancellationToken)) throw new NotFoundException();

        var connection = await persistence.FindProfileConnectionAsync(ownerId, parentId, request.ConnectionId, false, cancellationToken);
        if (connection is null)
        {
            throw new NotFoundException();
        }
        await persistence.DeleteProfileConnectionAsync(connection, cancellationToken);
        return;
    }
}
