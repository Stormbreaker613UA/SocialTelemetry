using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.ProfileAccess;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Infrastructure.Profiles;

public sealed class LocalProfileAccess(LocalProfileSelectionService selector, LocalProfileLifetimes lifetimes,
    AppDbContext database, Identity.LocalAccessValidation validation) : ILocalProfileAccess
{
    public async Task<ResolvedProfile> ResolveAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken)
    {
        var binding = await selector.ResolveAsync(assertedOwner, assertedVersion, cancellationToken);
        return new ResolvedProfile(binding, lifetimes.Capture(binding.SelectionVersion));
    }

    public Task ValidateAsync(LocalProfileBinding binding, CancellationToken cancellationToken) =>
        validation.ValidateAsync(binding, cancellationToken);
    public Task RecordActivityAsync(LocalProfileBinding binding, CancellationToken cancellationToken) =>
        validation.RecordActivityAsync(binding, cancellationToken);

    public Task<bool> RootExistsAsync(Guid owner, OwnershipRoot root, Guid id, CancellationToken cancellationToken) =>
        root switch
        {
            OwnershipRoot.PersonGroup => database.PersonGroups.AsNoTracking()
                .AnyAsync(group => group.Id == id && group.UserProfileId == owner, cancellationToken),
            OwnershipRoot.Person => database.People.AsNoTracking()
                .AnyAsync(person => person.Id == id && person.UserProfileId == owner, cancellationToken),
            OwnershipRoot.Interaction => database.Interactions.AsNoTracking()
                .AnyAsync(interaction => interaction.Id == id && interaction.UserProfileId == owner, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(root))
        };
}
