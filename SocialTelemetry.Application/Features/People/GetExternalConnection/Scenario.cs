using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileConnections;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.People.GetExternalConnection;

public sealed class Scenario(IProfileConnectionsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<PersonExternalConnection> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        var parentId = request.PersonId;
        if (!await persistence.PersonExistsAsync(ownerId, parentId, cancellationToken)) throw new NotFoundException();

        var connection = await persistence.FindPersonConnectionAsync(ownerId, parentId, request.ConnectionId, true, cancellationToken);
        if (connection is null)
        {
            throw new NotFoundException();
        }

        return connection;
    }
}
