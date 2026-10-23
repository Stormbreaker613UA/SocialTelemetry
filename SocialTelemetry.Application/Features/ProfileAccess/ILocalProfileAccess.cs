using SocialTelemetry.Application.Contracts.Profiles;

namespace SocialTelemetry.Application.Features.ProfileAccess;

public enum OwnershipRoot { Person, Interaction, PersonGroup }
public sealed record ResolvedProfile(LocalProfileBinding Binding, CancellationToken Lifetime);

public interface ILocalProfileAccess : ILocalProfileAdmission
{
    Task<ResolvedProfile> ResolveAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken);
    Task<bool> RootExistsAsync(Guid owner, OwnershipRoot root, Guid id, CancellationToken cancellationToken);
}
