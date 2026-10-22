using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;

namespace SocialTelemetry.Application.Features.Interactions.GetForPerson;

public sealed class Scenario(IInteractionPersistence persistence, LocalProfileOperation operation)
{
    public async Task<IReadOnlyList<InteractionSummary>> ExecuteAsync(Guid personId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        if (!await persistence.PersonExistsAsync(ownerId, personId, cancellationToken)) throw new NotFoundException();
        return await persistence.ReadForPersonAsync(ownerId, personId, cancellationToken);
    }
}
