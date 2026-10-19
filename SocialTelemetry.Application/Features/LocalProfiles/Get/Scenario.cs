namespace SocialTelemetry.Application.Features.LocalProfiles.Get;

public sealed class Scenario(ILocalProfileManagement profiles)
{
    public async Task<ProfileList> ExecuteAsync(CancellationToken cancellationToken)
    {
        var selection = await profiles.GetSelectionAsync(cancellationToken);
        var identities = await profiles.ListProfilesAsync(cancellationToken);
        return new ProfileList(selection, identities);
    }
}
