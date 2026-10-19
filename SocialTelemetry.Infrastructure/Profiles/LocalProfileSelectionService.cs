using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Infrastructure.Profiles;

public sealed class LocalProfileSelectionService(AppDbContext database, LocalProfileLifetimes lifetimes)
{
    public async Task<LocalProfileSelection> GetAsync(CancellationToken cancellationToken)
    {
        var existing = await database.Set<LocalProfileSelection>().AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing;

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockLocalProfileSelectionAsync(cancellationToken);
        var selection = await database.Set<LocalProfileSelection>().SingleAsync(cancellationToken);
        // Adopt only an unambiguous legacy profile; credentials are never adopted here.
        var profiles = await database.UserProfiles.AsNoTracking().Select(profile => profile.Id).Take(2).ToListAsync(cancellationToken);
        if (selection.UserProfileId is null && profiles.Count == 1 &&
            !await database.Users.AnyAsync(account => account.UserProfileId == profiles[0], cancellationToken))
        {
            selection.UserProfileId = profiles[0];
            selection.LegacySingleProfileAccess = true;
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return selection;
    }

    public async Task<UserProfile> CreateAsync(string displayName, Guid expectedVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
            throw new InvalidInputException();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockLocalProfileSelectionAsync(cancellationToken);
        var selection = await database.Set<LocalProfileSelection>().SingleAsync(cancellationToken);
        CheckVersion(selection, expectedVersion);
        var profile = new UserProfile { Id = Guid.NewGuid(), DisplayName = displayName.Trim() };
        database.UserProfiles.Add(profile);
        // Creating a profile does not switch or cancel the active owner. Once multiple identities
        // exist, old unasserted clients must reload instead of choosing a first row.
        selection.LegacySingleProfileAccess = false;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }

    public async Task<LocalProfileSelection> SelectAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken)
    {
        // Serialize publication with commits inside this local host; callbacks run after commit.
        await lifetimes.SelectionGate.WaitAsync(cancellationToken);
        try { return await SelectCoreAsync(userProfileId, expectedVersion, cancellationToken); }
        finally { lifetimes.SelectionGate.Release(); }
    }

    private async Task<LocalProfileSelection> SelectCoreAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockLocalProfileSelectionAsync(cancellationToken);
        var selection = await database.Set<LocalProfileSelection>().SingleAsync(cancellationToken);
        CheckVersion(selection, expectedVersion);
        if (!await database.UserProfiles.AnyAsync(profile => profile.Id == userProfileId, cancellationToken))
            throw new NotFoundException();
        // No cookie/unlock mechanism exists in this increment. Never bypass a linked Account.
        if (await database.Users.AnyAsync(account => account.UserProfileId == userProfileId, cancellationToken))
            throw new LocalProfileAccessException(LocalProfileFailure.ProtectedAccessUnavailable);
        if (selection.UserProfileId != userProfileId || selection.LegacySingleProfileAccess)
        {
            selection.UserProfileId = userProfileId;
            selection.Version = Guid.NewGuid();
            selection.LegacySingleProfileAccess = false;
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        lifetimes.Publish(selection.Version);
        return selection;
    }

    public async Task<LocalProfileBinding> ResolveAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken)
    {
        // Refresh a cooperating host's committed selection without allowing an older local
        // resolver to publish after a newer local switch. SQL remains the final commit guard.
        await lifetimes.SelectionGate.WaitAsync(cancellationToken);
        try
        {
            var binding = await ResolveCoreAsync(assertedOwner, assertedVersion, cancellationToken);
            lifetimes.Publish(binding.SelectionVersion);
            return binding;
        }
        finally { lifetimes.SelectionGate.Release(); }
    }

    private async Task<LocalProfileBinding> ResolveCoreAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken)
    {
        var selection = await GetAsync(cancellationToken);
        if (selection.UserProfileId is not { } owner)
            throw new LocalProfileAccessException(LocalProfileFailure.SelectionRequired);
        if (assertedOwner is null || assertedVersion is null)
        {
            if (assertedOwner is not null || assertedVersion is not null || !selection.LegacySingleProfileAccess ||
                await database.UserProfiles.AsNoTracking().CountAsync(cancellationToken) != 1)
                throw new LocalProfileAccessException(LocalProfileFailure.AssertionRequired);
        }
        else if (assertedOwner != owner || assertedVersion != selection.Version)
            throw new LocalProfileAccessException(LocalProfileFailure.SelectionChanged);
        if (await database.Users.AsNoTracking().AnyAsync(account => account.UserProfileId == owner, cancellationToken))
            throw new LocalProfileAccessException(LocalProfileFailure.ProtectedAccessUnavailable);
        return new LocalProfileBinding(owner, selection.Version);
    }

    private static void CheckVersion(LocalProfileSelection selection, Guid expectedVersion)
    {
        if (expectedVersion == Guid.Empty || selection.Version != expectedVersion)
            throw new LocalProfileAccessException(LocalProfileFailure.SelectionChanged);
    }
}
