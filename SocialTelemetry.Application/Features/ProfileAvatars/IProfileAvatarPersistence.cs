namespace SocialTelemetry.Application.Features.ProfileAvatars;

public interface IProfileAvatarPersistence
{
    Task<AvatarOwner> FindOwnerAsync(Guid ownerId, Guid? personId, bool readOnly, CancellationToken cancellationToken);
    Task<string> StageAsync(Stream content, CancellationToken cancellationToken);
    Task<bool> CompleteUploadAsync(string key, CancellationToken cancellationToken);
    Task SaveAvatarAsync(CancellationToken cancellationToken);
    Task DeleteUnreferencedAsync(string key);
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);
}
