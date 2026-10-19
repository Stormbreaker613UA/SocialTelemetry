using SocialTelemetry.Application.Contracts.Profiles;

namespace SocialTelemetry.Application.Features.ProfileAccess.Resolve;

public sealed class Scenario(ILocalProfileAccess access, LocalProfileOperation operation)
{
    public async Task<ResolvedProfile> ExecuteAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken)
    {
        var resolved = await access.ResolveAsync(assertedOwner, assertedVersion, cancellationToken);
        operation.Bind(resolved.Binding, resolved.Lifetime);
        return resolved;
    }
}
