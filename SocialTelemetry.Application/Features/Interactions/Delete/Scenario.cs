using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.Delete;

public sealed class Scenario(IInteractionPersistence persistence, LocalProfileOperation operation)
{
    public async Task<Interaction> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        var interaction = await persistence.FindForDeleteAsync(ownerId, request.Id, cancellationToken);

        if (interaction is null)
        {
            throw new NotFoundException();
        }

        foreach (var attachment in interaction.Attachments)
        {
            attachment.Status = AttachmentStatus.Deleting;
        }

        await persistence.MarkAttachmentsDeletingAsync(interaction, cancellationToken);

        foreach (var attachment in interaction.Attachments)
        {
            if (attachment.StorageKey is not null)
            {
                await persistence.DeleteMarkedFileAsync(attachment.StorageKey, cancellationToken);
            }
        }

        await persistence.DeleteInteractionAsync(interaction, cancellationToken);

        return interaction;
    }
}
