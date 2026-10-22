using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileConnections;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.People.DeleteExternalConnection;

public sealed class Scenario(IProfileConnectionsPersistence persistence, LocalProfileOperation operation)
{
    public async Task ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        var parentId = request.PersonId;
        if (!await persistence.PersonExistsAsync(ownerId, parentId, cancellationToken)) throw new NotFoundException();

        var connection = await persistence.FindPersonConnectionAsync(ownerId, parentId, request.ConnectionId, false, cancellationToken);
        if (connection is null)
        {
            throw new NotFoundException();
        }
        await persistence.DeletePersonConnectionAsync(connection, cancellationToken);
        return;
    }
}
