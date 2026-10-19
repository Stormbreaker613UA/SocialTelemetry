namespace SocialTelemetry.Application.Features.LocalProfiles.Select;

public sealed class Scenario(ILocalProfileManagement profiles)
{
    public Task<SelectionSnapshot> ExecuteAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken) =>
        profiles.SelectProfileAsync(userProfileId, expectedVersion, cancellationToken);
}
