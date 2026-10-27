using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Options;

namespace SocialTelemetry.Application.Features.ProfileAvatars;

public sealed class ProfileAvatarService(
    IProfileAvatarPersistence persistence, LocalProfileOperation operation, UploadOptions options)
{
    public async Task<AvatarOwner> FindOwnerAsync(Guid? personId, CancellationToken cancellationToken, bool readOnly = false)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        return await persistence.FindOwnerAsync(ownerId, personId, readOnly, cancellationToken);
    }

    public async Task UploadAsync(AvatarOwner owner, byte[] bytes, string mimeType, CancellationToken cancellationToken)
    {
        var ownerId = RequireOwner(owner);
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        owner = await persistence.FindOwnerAsync(ownerId, owner.PersonId, readOnly: false, cancellationToken);
        if (bytes.Length == 0 || bytes.LongLength > options.AvatarMaxBytes)
            throw new FeatureValidationException("A non-empty avatar within the configured upload limit is required.");
        if (!IsValidImage(bytes, mimeType))
            throw new FeatureValidationException("Avatar must be PNG, JPEG, or WebP with matching content.");

        using var content = new MemoryStream(bytes, writable: false);
        var key = await persistence.StageAsync(content, cancellationToken);
        try
        {
            if (!await persistence.CompleteUploadAsync(key, cancellationToken))
                throw new IOException("The staged avatar is unavailable.");
            owner.SetAvatar(key, mimeType);
            await persistence.SaveAvatarAsync(cancellationToken);
        }
        catch
        {
            await persistence.DeleteUnreferencedAsync(key);
            throw;
        }
    }

    public async Task<(Stream Content, string MimeType)> OpenAsync(AvatarOwner owner, CancellationToken cancellationToken)
    {
        var ownerId = RequireOwner(owner);
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        owner = await persistence.FindOwnerAsync(ownerId, owner.PersonId, readOnly: true, cancellationToken);
        var key = owner.StorageKey;
        var mimeType = owner.MimeType;
        if (key is null || mimeType is null) throw new NotFoundException();
        var content = await persistence.OpenReadAsync(key, cancellationToken) ?? throw new NotFoundException();
        return (content, mimeType);
    }

    public async Task DeleteAsync(AvatarOwner owner, CancellationToken cancellationToken)
    {
        var ownerId = RequireOwner(owner);
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        owner = await persistence.FindOwnerAsync(ownerId, owner.PersonId, readOnly: false, cancellationToken);
        owner.SetAvatar(null, null);
        await persistence.SaveAvatarAsync(cancellationToken);
    }

    public static bool IsValidImage(byte[] bytes, string mimeType) => mimeType switch
    {
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    private Guid RequireOwner(AvatarOwner owner)
    {
        var ownerId = operation.RequireOwner();
        if (owner.UserProfileId != ownerId) throw new NotFoundException();
        return ownerId;
    }
}
