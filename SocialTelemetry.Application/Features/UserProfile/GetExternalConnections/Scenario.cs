using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileConnections;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.UserProfile.GetExternalConnections;

public sealed class Scenario(IProfileConnectionsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<IReadOnlyList<UserProfileExternalConnection>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        var parentId = ownerId;
        if (!await persistence.ProfileExistsAsync(ownerId, parentId, cancellationToken)) throw new NotFoundException();

        return await persistence.ListProfileConnectionsAsync(ownerId, parentId, cancellationToken);
    }
}
