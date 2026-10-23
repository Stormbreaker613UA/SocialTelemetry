using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.UserProfile;

namespace SocialTelemetry.Application.Features.LocalProfiles.Rename;

public sealed class Scenario(IUserProfilePersistence persistence, LocalProfileOperation operation)
{
    public async Task<RenamedProfile> ExecuteAsync(Guid id, string displayName, Guid expectedVersion,
        CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        if (id != owner) throw new NotFoundException();
        SelectionPolicy.CheckVersion(new(owner, operation.Binding!.SelectionVersion, false), expectedVersion);
        if (string.IsNullOrWhiteSpace(displayName)) throw new InvalidInputException();
        var profile = await persistence.FindProfileAsync(owner, false, cancellationToken)
            ?? throw new NotFoundException();
        profile.DisplayName = displayName.Trim();
        await persistence.SaveProfileAsync(profile, cancellationToken);
        return new(owner, profile.DisplayName, expectedVersion);
    }
}
