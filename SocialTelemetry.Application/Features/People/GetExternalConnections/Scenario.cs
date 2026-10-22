using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileConnections;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.People.GetExternalConnections;

public sealed class Scenario(IProfileConnectionsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<IReadOnlyList<PersonExternalConnection>> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        var parentId = request.PersonId;
        if (!await persistence.PersonExistsAsync(ownerId, parentId, cancellationToken)) throw new NotFoundException();

        return await persistence.ListPersonConnectionsAsync(ownerId, parentId, cancellationToken);
    }
}
