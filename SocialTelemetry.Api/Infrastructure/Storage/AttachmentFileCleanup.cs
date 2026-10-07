using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public static class AttachmentFileCleanup
{
    public static async Task DeleteForMarkedAttachmentsAsync(AppDbContext database, IAttachmentStorage storage,
        string key, CancellationToken cancellationToken)
    {
        var normalizedKey = key.ToLowerInvariant();
        // Preserve a shared file while any usable or pending attachment still owns it.
        if (await database.InteractionAttachments.AsNoTracking().AnyAsync(attachment =>
            attachment.StorageKey != null && attachment.StorageKey.ToLower() == normalizedKey &&
            attachment.Status != AttachmentStatus.Deleting, cancellationToken)) return;

        await storage.DeleteAsync(key, cancellationToken);
        await storage.DeleteStagedAsync(key, cancellationToken);
    }

    public static Task<bool> IsReferencedAsync(AppDbContext database, string key, CancellationToken cancellationToken)
    {
        var normalizedKey = key.ToLowerInvariant();
        return database.InteractionAttachments.AsNoTracking().AnyAsync(attachment =>
            attachment.StorageKey != null && attachment.StorageKey.ToLower() == normalizedKey, cancellationToken);
    }
}
