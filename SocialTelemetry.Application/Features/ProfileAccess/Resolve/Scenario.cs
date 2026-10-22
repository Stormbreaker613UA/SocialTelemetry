using SocialTelemetry.Application.Contracts.Profiles;

namespace SocialTelemetry.Application.Features.ProfileAccess.Resolve;

public sealed class Scenario(ILocalProfileAccess access, LocalProfileOperation operation)
{
    public async Task<ResolvedProfile> ExecuteAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken)
    {
        var resolved = await access.ResolveAsync(assertedOwner, assertedVersion, cancellationToken);
        operation.Bind(resolved.Binding, resolved.Lifetime);
        operation.Authorize(access);
        return resolved;
    }

    public async Task RestoreAsync(LocalProfileBinding captured, CancellationToken lifetime, CancellationToken cancellationToken)
    {
        // Callback restoration uses only its original captured proof, never current selection
        // as a replacement owner or a newly authenticated callback browser.
        await access.ValidateAsync(captured, cancellationToken);
        operation.Bind(captured, lifetime);
        operation.Authorize(access);
    }
}
