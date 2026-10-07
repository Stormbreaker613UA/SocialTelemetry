using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class ProfileAvatarCleanup(
    [FromKeyedServices("avatars")] IFileStorage storage,
    ILogger<ProfileAvatarCleanup> logger)
{
    public async Task<string[]> FindObsoleteKeysAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>();
        var deletedProfiles = new List<Guid>();
        foreach (var entry in database.ChangeTracker.Entries())
        {
            if (entry.Entity is not (UserProfile or Person)) continue;
            if (entry.State == EntityState.Deleted ||
                (entry.State == EntityState.Modified && entry.Property("AvatarStorageKey").IsModified))
            {
                if (entry.Property("AvatarStorageKey").OriginalValue is string key) keys.Add(key);
            }
            if (entry.Entity is UserProfile profile && entry.State == EntityState.Deleted)
                deletedProfiles.Add(profile.Id);
        }
        if (deletedProfiles.Count > 0)
        {
            var cascadeKeys = await database.People.AsNoTracking()
                .Where(person => deletedProfiles.Contains(person.UserProfileId) && person.AvatarStorageKey != null)
                .Select(person => person.AvatarStorageKey!).ToListAsync(cancellationToken);
            keys.UnionWith(cascadeKeys);
        }
        return keys.ToArray();
    }

    public async Task DeleteUnreferencedAsync(AppDbContext database, IEnumerable<string> keys)
    {
        foreach (var key in keys.Distinct())
        {
            try
            {
                // A failed/ambiguous commit or a shared reference must never cause live media deletion.
                if (await IsReferencedAsync(database, key, CancellationToken.None)) continue;
                await storage.DeleteAsync(key, CancellationToken.None);
                await storage.DeleteStagedAsync(key, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning("Avatar cleanup deferred for {StorageKey} ({ExceptionType})", key, exception.GetType().Name);
            }
        }
    }

    public static async Task<bool> IsReferencedAsync(AppDbContext database, string key, CancellationToken cancellationToken)
    {
        var normalizedKey = key.ToLowerInvariant();
        return await database.UserProfiles.AsNoTracking().AnyAsync(profile => profile.AvatarStorageKey != null &&
            profile.AvatarStorageKey.ToLower() == normalizedKey, cancellationToken) ||
            await database.People.AsNoTracking().AnyAsync(person => person.AvatarStorageKey != null &&
                person.AvatarStorageKey.ToLower() == normalizedKey, cancellationToken);
    }
}
