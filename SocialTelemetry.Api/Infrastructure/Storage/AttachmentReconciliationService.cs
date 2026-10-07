using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Observability;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class AttachmentReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<AttachmentReconciliationService> logger,
    IOptions<StorageMaintenanceOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Attachment reconciliation could not complete ({ExceptionType})", exception.GetType().Name);
            // Hosting logs startup exceptions outside the API's safe exception handler.
            throw new InvalidOperationException("Attachment reconciliation failed. Verify database migrations and local storage access before restarting.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        using var operation = SocialTelemetryTelemetry.Start("storage.reconcile_attachments", cancellationToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        var unfinishedAttachments = await dbContext.InteractionAttachments
            .AsNoTracking()
            .Where(attachment => attachment.Status != AttachmentStatus.Ready)
            .ToListAsync(cancellationToken);

        foreach (var attachment in unfinishedAttachments)
        {
            SocialTelemetryTelemetry.RecordReconciliation("attachments", "inspected");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                dbContext.Attach(attachment);
                await RecoverAttachmentAsync(dbContext, storage, attachment, cancellationToken);
                SocialTelemetryTelemetry.RecordReconciliation("attachments", "recovered");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                SocialTelemetryTelemetry.RecordReconciliation("attachments", "failed");
                logger.LogWarning("Recovery failed for Attachment {AttachmentId} ({ExceptionType}); its state is retained for retry",
                    attachment.Id, exception.GetType().Name);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        var readyFiles = await dbContext.InteractionAttachments
            .AsNoTracking()
            .Where(attachment => attachment.Status == AttachmentStatus.Ready && attachment.Type != AttachmentType.Text)
            .Select(attachment => new { attachment.Id, attachment.StorageKey })
            .ToListAsync(cancellationToken);
        foreach (var attachment in readyFiles)
        {
            SocialTelemetryTelemetry.RecordReconciliation("attachments", "inspected");
            try
            {
                if (attachment.StorageKey is null || !await storage.ExistsAsync(attachment.StorageKey, cancellationToken))
                {
                    SocialTelemetryTelemetry.RecordReconciliation("attachments", "missing");
                    logger.LogWarning("Ready Attachment {AttachmentId} with key {StorageKey} is missing its file; metadata remains unchanged",
                        attachment.Id, attachment.StorageKey);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                SocialTelemetryTelemetry.RecordReconciliation("attachments", "failed");
                logger.LogWarning("Could not inspect Ready Attachment {AttachmentId} ({ExceptionType}); metadata remains unchanged",
                    attachment.Id, exception.GetType().Name);
            }
        }

        var referencedKeys = await dbContext.InteractionAttachments
            .AsNoTracking()
            .Where(attachment => attachment.StorageKey != null)
            .Select(attachment => attachment.StorageKey!)
            .ToListAsync(cancellationToken);
        var referencedKeySet = referencedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTimeOffset.UtcNow - options.Value.OrphanSafetyAge;
        await RemoveOrphansAsync(dbContext, storage, storage.EnumerateFiles(), referencedKeySet, cutoff, staged: false, cancellationToken);
        await RemoveOrphansAsync(dbContext, storage, storage.EnumerateStagedFiles(), referencedKeySet, cutoff, staged: true, cancellationToken);
        operation.Complete();
    }

    private async Task RecoverAttachmentAsync(
        AppDbContext dbContext, IAttachmentStorage storage, InteractionAttachment attachment, CancellationToken cancellationToken)
    {
        if (attachment.Status == AttachmentStatus.Pending)
        {
            var uploadComplete = attachment.Type == AttachmentType.Text ||
                (attachment.StorageKey is not null && await storage.CompleteUploadAsync(attachment.StorageKey, cancellationToken));
            if (uploadComplete)
            {
                attachment.Status = AttachmentStatus.Ready;
            }
            else
            {
                dbContext.InteractionAttachments.Remove(attachment);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Pending Attachment {AttachmentId} with key {StorageKey} recovery completed; file available {FileAvailable}",
                attachment.Id, attachment.StorageKey, uploadComplete);
        }
        else if (attachment.Status == AttachmentStatus.Deleting)
        {
            if (attachment.StorageKey is not null)
            {
                await AttachmentFileCleanup.DeleteForMarkedAttachmentsAsync(
                    dbContext, storage, attachment.StorageKey, cancellationToken);
            }

            dbContext.InteractionAttachments.Remove(attachment);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Finished deletion for Attachment {AttachmentId} with key {StorageKey}", attachment.Id, attachment.StorageKey);
        }
        else
        {
            logger.LogWarning("Attachment {AttachmentId} has unsupported status {AttachmentStatus}", attachment.Id, attachment.Status);
        }
    }

    private async Task RemoveOrphansAsync(
        AppDbContext dbContext,
        IAttachmentStorage storage,
        IEnumerable<StoredAttachmentFile> files,
        HashSet<string> referencedKeys,
        DateTimeOffset cutoff,
        bool staged,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            SocialTelemetryTelemetry.RecordReconciliation("attachments", "inspected");
            cancellationToken.ThrowIfCancellationRequested();
            if (referencedKeys.Contains(file.StorageKey) || file.LastModified >= cutoff)
            {
                continue;
            }

            try
            {
                // Recheck current ownership: the earlier snapshot must not authorize deletion.
                if (await AttachmentFileCleanup.IsReferencedAsync(dbContext, file.StorageKey, cancellationToken)) continue;
                if (staged)
                {
                    await storage.DeleteStagedAsync(file.StorageKey, cancellationToken);
                }
                else
                {
                    await storage.DeleteAsync(file.StorageKey, cancellationToken);
                }

                SocialTelemetryTelemetry.RecordReconciliation("attachments", "orphan_deleted");
                logger.LogInformation("Removed orphan attachment file {StorageKey} from {StorageArea}",
                    file.StorageKey, staged ? "staging" : "final storage");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                SocialTelemetryTelemetry.RecordReconciliation("attachments", "failed");
                logger.LogWarning("Orphan cleanup failed for {StorageKey} ({ExceptionType})", file.StorageKey, exception.GetType().Name);
            }
        }
    }
}
