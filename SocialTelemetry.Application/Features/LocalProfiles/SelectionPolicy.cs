using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;

namespace SocialTelemetry.Application.Features.LocalProfiles;

// Pure decisions are shared with the short protected persistence operations.
public static class SelectionPolicy
{
    public static bool CanAdoptLegacy(IReadOnlyList<Guid> profiles, bool accountLinked) =>
        profiles.Count == 1 && !accountLinked;

    public static void CheckVersion(SelectionSnapshot selection, Guid expectedVersion)
    {
        if (expectedVersion == Guid.Empty || selection.Version != expectedVersion)
            throw new LocalProfileAccessException(LocalProfileFailure.SelectionChanged);
    }

    public static void CheckTargetExists(bool exists)
    {
        if (!exists) throw new NotFoundException();
    }

    public static bool ShouldSwitch(SelectionSnapshot selection, Guid userProfileId) =>
        selection.UserProfileId != userProfileId || selection.LegacySingleProfileAccess;

    public static bool NeedsProfileCount(SelectionSnapshot selection, Guid? assertedOwner, Guid? assertedVersion) =>
        selection.UserProfileId is not null && assertedOwner is null && assertedVersion is null && selection.LegacySingleProfileAccess;

    public static void CheckAssertions(SelectionSnapshot selection, Guid? assertedOwner, Guid? assertedVersion,
        int? profileCount)
    {
        if (selection.UserProfileId is not { } owner)
            throw new LocalProfileAccessException(LocalProfileFailure.SelectionRequired);
        if (assertedOwner is null || assertedVersion is null)
        {
            if (assertedOwner is not null || assertedVersion is not null || !selection.LegacySingleProfileAccess || profileCount != 1)
                throw new LocalProfileAccessException(LocalProfileFailure.AssertionRequired);
        }
        else if (assertedOwner != owner || assertedVersion != selection.Version)
            throw new LocalProfileAccessException(LocalProfileFailure.SelectionChanged);
    }

    public static void CheckProtection(bool accountLinked)
    {
        if (accountLinked)
            throw new LocalProfileAccessException(LocalProfileFailure.ProtectedAccessUnavailable);
    }
}
