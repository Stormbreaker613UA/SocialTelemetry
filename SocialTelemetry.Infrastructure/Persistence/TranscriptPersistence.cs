using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class TranscriptPersistence(AppDbContext database) : ITranscriptPersistence
{
    public async Task<TranscriptSource> LoadSourceAsync(Guid ownerId, Guid interactionId, Guid attachmentId, CancellationToken cancellationToken) =>
        await database.InteractionAttachments.AsNoTracking()
            .Where(attachment => attachment.Interaction.UserProfileId == ownerId &&
                attachment.Id == attachmentId && attachment.InteractionId == interactionId)
            .Select(attachment => new TranscriptSource(attachment.Interaction.UserProfileId, attachment.Type, attachment.Status, attachment.StorageKey))
            .SingleOrDefaultAsync(cancellationToken) ?? throw new NotFoundException();

    public Task<AttachmentTranscript?> ReadTranscriptAsync(Guid ownerId, Guid attachmentId, CancellationToken cancellationToken) =>
        database.AttachmentTranscripts.AsNoTracking().SingleOrDefaultAsync(transcript =>
            transcript.Attachment.Interaction.UserProfileId == ownerId && transcript.AttachmentId == attachmentId, cancellationToken);

    public Task<AttachmentTranscript?> ReadReadyTranscriptAsync(Guid ownerId, Guid interactionId, Guid attachmentId, CancellationToken cancellationToken) =>
        database.AttachmentTranscripts.AsNoTracking().SingleOrDefaultAsync(transcript =>
            transcript.Attachment.Interaction.UserProfileId == ownerId && transcript.AttachmentId == attachmentId &&
            transcript.Attachment.InteractionId == interactionId && transcript.Attachment.Type == AttachmentType.Audio &&
            transcript.Attachment.Status == AttachmentStatus.Ready, cancellationToken);

    public async Task<TranscriptMutation> CommitTranscriptAsync(Guid ownerId, Guid interactionId, Guid attachmentId,
        Action<TranscriptSource> validateSource, Func<AttachmentTranscript?, TranscriptMutation> decide,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.LockAttachmentAnalysisContextAsync(attachmentId, interactionId, cancellationToken);
            var source = await LoadSourceAsync(ownerId, interactionId, attachmentId, cancellationToken);
            validateSource(source);
            var transcript = await database.AttachmentTranscripts.SingleOrDefaultAsync(current =>
                current.Attachment.Interaction.UserProfileId == ownerId && current.AttachmentId == attachmentId, cancellationToken);
            var mutation = decide(transcript);
            if (mutation.SaveChanges)
            {
                if (transcript is null) database.AttachmentTranscripts.Add(mutation.Transcript);
                await database.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return mutation;
        }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(); }
        catch (Exception exception) when (AnalysisPersistence.IsConcurrentSourceChange(exception)) { throw new ConflictException(); }
    }
}
