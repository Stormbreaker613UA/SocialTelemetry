using ProfileEntity = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.LocalProfiles;

public sealed record SelectionSnapshot(Guid? UserProfileId, Guid Version, bool LegacySingleProfileAccess);
public sealed record ProfileIdentity(Guid Id, string DisplayName, bool AccountLinked)
{
    public string Source => "Local";
    public string Protection { get; init; } = "Unprotected";
}
public sealed record ProfileList(SelectionSnapshot Selection, IReadOnlyList<ProfileIdentity> Profiles)
{
    public string Mode => "Local";
}

public interface ILocalProfileManagement
{
    Task<SelectionSnapshot> GetSelectionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProfileIdentity>> ListProfilesAsync(CancellationToken cancellationToken);
    Task<ProfileEntity> CreateProfileAsync(Guid expectedVersion, Func<ProfileEntity> create, CancellationToken cancellationToken);
    Task<SelectionSnapshot> SelectProfileAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken);
}
