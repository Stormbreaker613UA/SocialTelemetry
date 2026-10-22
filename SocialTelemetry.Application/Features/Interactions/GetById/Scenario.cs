using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;

namespace SocialTelemetry.Application.Features.Interactions.GetById;

public sealed class Scenario(IInteractionPersistence persistence, LocalProfileOperation operation)
{
    public async Task<InteractionDetails> ExecuteAsync(Guid interactionId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        return await persistence.ReadInteractionAsync(ownerId, interactionId, cancellationToken) ?? throw new NotFoundException();
    }
}
