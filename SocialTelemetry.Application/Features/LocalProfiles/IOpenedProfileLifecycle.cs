namespace SocialTelemetry.Application.Features.LocalProfiles;

public sealed record RenamedProfile(Guid UserProfileId, string DisplayName, Guid Version);
public sealed record DeletedProfile(Guid? UserProfileId, Guid Version, bool CleanupPending);

public interface IOpenedProfileLifecycle
{
    Task<DeletedProfile> DeleteAsync(Guid userProfileId, Guid expectedVersion, string? currentPassword,
        CancellationToken cancellationToken);
}
