using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.GetTranscript;

public sealed class Scenario(ITranscriptPersistence persistence, LocalProfileOperation operation)
{
    public async Task<AttachmentTranscript> ExecuteAsync(Guid interactionId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        var attachment = await persistence.LoadSourceAsync(ownerId, interactionId, attachmentId, cancellationToken);
        if (attachment.Type != AttachmentType.Audio)
            throw new FeatureValidationException("Transcripts require an Audio attachment.");
        if (attachment.Status != AttachmentStatus.Ready) throw new ConflictException();
        return await persistence.ReadReadyTranscriptAsync(ownerId, interactionId, attachmentId, cancellationToken)
            ?? throw new NotFoundException();
    }
}
