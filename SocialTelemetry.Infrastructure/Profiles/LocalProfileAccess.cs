using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Features.ProfileAccess;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Infrastructure.Profiles;

public sealed class LocalProfileAccess(LocalProfileSelectionService selector, LocalProfileLifetimes lifetimes,
    AppDbContext database) : ILocalProfileAccess
{
    public async Task<ResolvedProfile> ResolveAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken)
    {
        var binding = await selector.ResolveAsync(assertedOwner, assertedVersion, cancellationToken);
        return new ResolvedProfile(binding, lifetimes.Capture(binding.SelectionVersion));
    }

    public Task<bool> RootExistsAsync(Guid owner, OwnershipRoot root, Guid id, CancellationToken cancellationToken) =>
        root switch
        {
            OwnershipRoot.Person => database.People.AsNoTracking()
                .AnyAsync(person => person.Id == id && person.UserProfileId == owner, cancellationToken),
            OwnershipRoot.Interaction => database.Interactions.AsNoTracking()
                .AnyAsync(interaction => interaction.Id == id && interaction.UserProfileId == owner, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(root))
        };
}
