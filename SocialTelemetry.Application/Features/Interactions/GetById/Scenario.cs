using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;

namespace SocialTelemetry.Application.Features.Interactions.GetById;

public sealed class Scenario(IInteractionPersistence persistence, LocalProfileOperation operation)
{
    public async Task<InteractionDetails> ExecuteAsync(Guid interactionId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        return await persistence.ReadInteractionAsync(ownerId, interactionId, cancellationToken) ?? throw new NotFoundException();
    }
}
