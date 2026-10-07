using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class ProfileAvatarService(
    AppDbContext database,
    [FromKeyedServices("avatars")] IFileStorage storage,
    ProfileAvatarCleanup cleanup)
{
    public async Task<object> FindOwnerAsync(Guid? personId, CancellationToken cancellationToken, bool readOnly = false)
    {
        if (personId is Guid id)
        {
            var people = readOnly ? database.People.AsNoTracking() : database.People;
            return await people.SingleOrDefaultAsync(person => person.Id == id, cancellationToken)
                ?? throw new NotFoundException();
        }
        var profiles = readOnly ? database.UserProfiles.AsNoTracking() : database.UserProfiles;
        return await profiles.OrderBy(profile => profile.Id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();
    }

    public async Task UploadAsync(object owner, byte[] bytes, string mimeType, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(bytes, writable: false);
        var key = await storage.SaveAsync(content, cancellationToken);
        try
        {
            if (!await storage.CompleteUploadAsync(key, cancellationToken))
                throw new IOException("The staged avatar is unavailable.");
            SetAvatar(owner, key, mimeType);
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await cleanup.DeleteUnreferencedAsync(database, [key]);
            throw new ConflictException();
        }
        catch
        {
            await cleanup.DeleteUnreferencedAsync(database, [key]);
            throw;
        }
    }

    public async Task<(Stream Content, string MimeType)> OpenAsync(object owner, CancellationToken cancellationToken)
    {
        var (key, mimeType) = GetAvatar(owner);
        if (key is null || mimeType is null) throw new NotFoundException();
        var content = await storage.OpenReadAsync(key, cancellationToken) ?? throw new NotFoundException();
        return (content, mimeType);
    }

    public async Task DeleteAsync(object owner, CancellationToken cancellationToken)
    {
        SetAvatar(owner, null, null);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException();
        }
    }

    public static bool IsValidImage(byte[] bytes, string mimeType) => mimeType switch
    {
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    private static (string? Key, string? MimeType) GetAvatar(object owner) => owner switch
    {
        Person person => (person.AvatarStorageKey, person.AvatarMimeType),
        UserProfile profile => (profile.AvatarStorageKey, profile.AvatarMimeType),
        _ => throw new ArgumentException("Unsupported avatar owner.", nameof(owner))
    };

    private static void SetAvatar(object owner, string? key, string? mimeType)
    {
        if (owner is Person person)
        {
            person.AvatarStorageKey = key;
            person.AvatarMimeType = mimeType;
        }
        else if (owner is UserProfile profile)
        {
            profile.AvatarStorageKey = key;
            profile.AvatarMimeType = mimeType;
        }
        else
        {
            throw new ArgumentException("Unsupported avatar owner.", nameof(owner));
        }
    }
}
