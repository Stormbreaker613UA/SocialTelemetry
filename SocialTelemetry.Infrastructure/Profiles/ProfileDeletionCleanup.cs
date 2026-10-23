using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Infrastructure.Profiles;

public sealed class ProfileDeletionCleanup(AppDbContext database, ChatGptConnection connection,
    ILogger<ProfileDeletionCleanup> logger)
{
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var owners = await database.Set<ProfileDeletionCleanupMarker>().AsNoTracking()
            .Select(marker => marker.UserProfileId).ToArrayAsync(cancellationToken);
        foreach (var owner in owners) await TryCleanAsync(owner, cancellationToken);
    }

    public async Task<bool> TryCleanAsync(Guid owner, CancellationToken cancellationToken)
    {
        try
        {
            if (!await database.Set<ProfileDeletionCleanupMarker>().AsNoTracking()
                    .AnyAsync(marker => marker.UserProfileId == owner, cancellationToken)) return true;
            await connection.ForgetDeletedOwnerAsync(owner, cancellationToken);
            await database.Set<ProfileDeletionCleanupMarker>().Where(marker => marker.UserProfileId == owner)
                .ExecuteDeleteAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Addressed profile credential cleanup deferred ({ExceptionType})", exception.GetType().Name);
            return false;
        }
    }
}
