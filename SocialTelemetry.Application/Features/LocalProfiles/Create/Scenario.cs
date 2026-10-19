using SocialTelemetry.Application.Exceptions;
using ProfileEntity = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.LocalProfiles.Create;

public sealed class Scenario(ILocalProfileManagement profiles)
{
    public Task<ProfileEntity> ExecuteAsync(string displayName, Guid expectedVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
            throw new InvalidInputException();
        // Construct only after the persisted CAS succeeds, as in the existing protected operation.
        return profiles.CreateProfileAsync(expectedVersion,
            () => new ProfileEntity { Id = Guid.NewGuid(), DisplayName = displayName.Trim() }, cancellationToken);
    }
}
