using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Features.Interactions;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Infrastructure.Storage;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class AttachmentPersistence(
    AppDbContext database, IAttachmentStorage attachmentStorage, ILoggerFactory loggerFactory) : IAttachmentPersistence
{
    // Retain the existing technical event category across the physical relocation.
    private readonly ILogger logger = loggerFactory.CreateLogger("SocialTelemetry.Api.Features.Interactions.AddAttachment.Endpoint");

    public Task<bool> InteractionExistsAsync(Guid ownerId, Guid interactionId, CancellationToken cancellationToken) =>
        database.Interactions.AsNoTracking().AnyAsync(interaction =>
            interaction.UserProfileId == ownerId && interaction.Id == interactionId, cancellationToken);

    public Task<InteractionAttachment?> FindAttachmentAsync(Guid ownerId, Guid interactionId, Guid attachmentId, bool readOnly, CancellationToken cancellationToken)
    {
        var attachments = readOnly ? database.InteractionAttachments.AsNoTracking() : database.InteractionAttachments;
        return attachments.SingleOrDefaultAsync(attachment => attachment.Interaction.UserProfileId == ownerId &&
            attachment.InteractionId == interactionId && attachment.Id == attachmentId, cancellationToken);
    }

    public async Task AddPendingAttachmentAsync(InteractionAttachment attachment, CancellationToken cancellationToken)
    {
        database.InteractionAttachments.Add(attachment);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task CompletePendingAttachmentAsync(Guid ownerId, InteractionAttachment attachment, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockAttachmentAnalysisContextAsync(attachment.Id, attachment.InteractionId, cancellationToken);
        var status = await OwnedUpload(ownerId, attachment).AsNoTracking()
            .Select(existing => (AttachmentStatus?)existing.Status).SingleOrDefaultAsync(cancellationToken);
        if (status is not (AttachmentStatus.Pending or AttachmentStatus.Ready))
            throw new SocialTelemetry.Application.Exceptions.ConflictException();

        // This completion may reconcile its status only, never flush or discard caller work.
        var tracked = database.Entry(attachment);
        if (tracked.State is not (EntityState.Unchanged or EntityState.Modified) ||
            database.ChangeTracker.Entries().Any(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                (!ReferenceEquals(entry.Entity, attachment) || entry.Properties.Any(property =>
                    property.IsModified && property.Metadata.Name != nameof(InteractionAttachment.Status)))))
            throw new SocialTelemetry.Application.Exceptions.ConflictException();

        // Recovery can already have committed Ready for this exact upload. Refresh only
        // its concurrency token under protection; Deleting/missing/reparented rows fail above.
        tracked.Property(existing => existing.Status).OriginalValue = status.Value;
        attachment.Status = AttachmentStatus.Ready;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveAttachmentStatusAsync(InteractionAttachment attachment, CancellationToken cancellationToken) =>
        await database.SaveChangesAsync(cancellationToken);

    public Task DeleteMarkedFileAsync(string key, CancellationToken cancellationToken) =>
        AttachmentFileCleanup.DeleteForMarkedAttachmentsAsync(database, attachmentStorage, key, cancellationToken);

    public async Task DeleteAttachmentAsync(InteractionAttachment attachment, CancellationToken cancellationToken)
    {
        database.InteractionAttachments.Remove(attachment);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task CleanupFailedUploadAsync(Guid ownerId, InteractionAttachment attachment)
    {
        if (attachment.StorageKey is null)
        {
            return;
        }

        // Persist cleanup intent when possible; cancellation must not prevent recovery.
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(CancellationToken.None);
            await database.LockAttachmentAnalysisContextAsync(attachment.Id, attachment.InteractionId, CancellationToken.None);
            var unfinished = OwnedUpload(ownerId, attachment)
                .Where(existing => existing.Status == AttachmentStatus.Pending || existing.Status == AttachmentStatus.Deleting);
            if (await database.InteractionAttachments.AnyAsync(existing => existing.Id == attachment.Id, CancellationToken.None) &&
                !await unfinished.AnyAsync(CancellationToken.None)) return;
            await unfinished
                .ExecuteUpdateAsync(update => update.SetProperty(existingAttachment => existingAttachment.Status, AttachmentStatus.Deleting),
                    CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            logger.LogWarning("Could not persist cleanup intent for Attachment {AttachmentId} ({ExceptionType})",
                attachment.Id, cleanupException.GetType().Name);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(CancellationToken.None);
            await database.LockAttachmentAnalysisContextAsync(attachment.Id, attachment.InteractionId, CancellationToken.None);
            var deleting = OwnedUpload(ownerId, attachment).Where(existing => existing.Status == AttachmentStatus.Deleting);
            if (await database.InteractionAttachments.AnyAsync(existing => existing.Id == attachment.Id, CancellationToken.None) &&
                !await deleting.AnyAsync(CancellationToken.None)) return;
            await AttachmentFileCleanup.DeleteForMarkedAttachmentsAsync(
                database, attachmentStorage, attachment.StorageKey, CancellationToken.None);
            await deleting.ExecuteDeleteAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            logger.LogWarning("Cleanup incomplete for Attachment {AttachmentId} ({ExceptionType}); startup recovery will retry",
                attachment.Id, cleanupException.GetType().Name);
        }
    }

    private IQueryable<InteractionAttachment> OwnedUpload(Guid ownerId, InteractionAttachment attachment) =>
        database.InteractionAttachments.Where(existing => existing.Id == attachment.Id &&
            existing.InteractionId == attachment.InteractionId && existing.Interaction.UserProfileId == ownerId &&
            existing.StorageKey == attachment.StorageKey && existing.Type == attachment.Type && existing.MimeType == attachment.MimeType);
}
