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
            await database.InteractionAttachments.Where(attachment => attachment.Interaction.UserProfileId == ownerId)
                .Where(existingAttachment => existingAttachment.Id == attachment.Id)
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
            await AttachmentFileCleanup.DeleteForMarkedAttachmentsAsync(
                database, attachmentStorage, attachment.StorageKey, CancellationToken.None);
            await database.InteractionAttachments.Where(attachment => attachment.Interaction.UserProfileId == ownerId)
                .Where(existingAttachment => existingAttachment.Id == attachment.Id)
                .ExecuteDeleteAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            logger.LogWarning("Cleanup incomplete for Attachment {AttachmentId} ({ExceptionType}); startup recovery will retry",
                attachment.Id, cleanupException.GetType().Name);
        }
    }

}
