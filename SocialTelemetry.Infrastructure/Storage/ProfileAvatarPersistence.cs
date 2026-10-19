using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileAvatars;
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
            var people = readOnly ? database.People.AsNoTracking() : database.People;
            var person = await people.SingleOrDefaultAsync(person => person.Id == id && person.UserProfileId == ownerId, cancellationToken)
                ?? throw new NotFoundException();
            return new AvatarOwner(person);
        }
        var profiles = readOnly ? database.UserProfiles.AsNoTracking() : database.UserProfiles;
        var profile = await profiles.SingleOrDefaultAsync(profile => profile.Id == ownerId, cancellationToken)
            ?? throw new NotFoundException();
        return new AvatarOwner(profile);
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
