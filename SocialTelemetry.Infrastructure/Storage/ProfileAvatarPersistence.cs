using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileAvatars;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Infrastructure.Storage;

public sealed class ProfileAvatarPersistence(
    AppDbContext database,
    [FromKeyedServices("avatars")] IFileStorage storage,
    ProfileAvatarCleanup cleanup) : IProfileAvatarPersistence
{
    public async Task<AvatarOwner> FindOwnerAsync(Guid ownerId, Guid? personId, bool readOnly, CancellationToken cancellationToken)
    {
        if (personId is Guid id)
        {
            var person = await database.People.AsNoTracking()
                .SingleOrDefaultAsync(person => person.Id == id && person.UserProfileId == ownerId, cancellationToken)
                ?? throw new NotFoundException();
            if (!readOnly)
            {
                var tracked = database.ChangeTracker.Entries<Person>()
                    .SingleOrDefault(entry => entry.Entity.Id == id);
                if (tracked is null) database.People.Attach(person);
                else
                {
                    if (tracked.Entity.UserProfileId != ownerId ||
                        tracked.Property(person => person.UserProfileId).OriginalValue != ownerId)
                        throw new ConflictException();
                    RequireCurrentAvatar(tracked, person.AvatarStorageKey, person.AvatarMimeType);
                    person = tracked.Entity;
                }
            }
            return new AvatarOwner(person);
        }
        var profile = await database.UserProfiles.AsNoTracking().SingleOrDefaultAsync(profile => profile.Id == ownerId, cancellationToken)
            ?? throw new NotFoundException();
        if (!readOnly)
        {
            var tracked = database.ChangeTracker.Entries<UserProfile>()
                .SingleOrDefault(entry => entry.Entity.Id == ownerId);
            if (tracked is null) database.UserProfiles.Attach(profile);
            else
            {
                RequireCurrentAvatar(tracked, profile.AvatarStorageKey, profile.AvatarMimeType);
                profile = tracked.Entity;
            }
        }
        return new AvatarOwner(profile);
    }

    private static void RequireCurrentAvatar(EntityEntry tracked, string? key, string? mimeType)
    {
        // Read from the database even in a reused scope; never accept caller edits as media authority.
        // Preserve the original concurrency baseline rather than refreshing away a losing replacement.
        var keyProperty = tracked.Property(nameof(Person.AvatarStorageKey));
        var mimeProperty = tracked.Property(nameof(Person.AvatarMimeType));
        if (tracked.State == EntityState.Deleted || !Equals(keyProperty.OriginalValue, key) ||
            !Equals(keyProperty.CurrentValue, key) || !Equals(mimeProperty.OriginalValue, mimeType) ||
            !Equals(mimeProperty.CurrentValue, mimeType))
            throw new ConflictException();
    }

    public Task<string> StageAsync(Stream content, CancellationToken cancellationToken) =>
        storage.SaveAsync(content, cancellationToken);

    public Task<bool> CompleteUploadAsync(string key, CancellationToken cancellationToken) =>
        storage.CompleteUploadAsync(key, cancellationToken);

    public async Task SaveAvatarAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException();
        }
    }

    public Task DeleteUnreferencedAsync(string key) => cleanup.DeleteUnreferencedAsync(database, [key]);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        storage.OpenReadAsync(key, cancellationToken);
}
