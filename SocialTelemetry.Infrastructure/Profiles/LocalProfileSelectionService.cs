using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.LocalProfiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Infrastructure.Profiles;

public sealed class LocalProfileSelectionService(AppDbContext database, LocalProfileLifetimes lifetimes) : ILocalProfileManagement
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
        if (selection.UserProfileId is null && SelectionPolicy.CanAdoptLegacy(profiles, profiles.Count == 1 &&
                await database.Users.AnyAsync(account => account.UserProfileId == profiles[0], cancellationToken)))
        {
            selection.UserProfileId = profiles[0];
            selection.LegacySingleProfileAccess = true;
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return selection;
    }

    public Task<UserProfile> CreateAsync(string displayName, Guid expectedVersion, CancellationToken cancellationToken) =>
        new SocialTelemetry.Application.Features.LocalProfiles.Create.Scenario(this)
            .ExecuteAsync(displayName, expectedVersion, cancellationToken);

    public async Task<UserProfile> CreateProfileAsync(Guid expectedVersion, Func<UserProfile> create, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockLocalProfileSelectionAsync(cancellationToken);
        var selection = await database.Set<LocalProfileSelection>().SingleAsync(cancellationToken);
        CheckVersion(selection, expectedVersion);
        var profile = create();
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
        SelectionPolicy.CheckTargetExists(await database.UserProfiles.AnyAsync(profile => profile.Id == userProfileId, cancellationToken));
        // No cookie/unlock mechanism exists in this increment. Never bypass a linked Account.
        SelectionPolicy.CheckProtection(await database.Users.AnyAsync(account => account.UserProfileId == userProfileId, cancellationToken));
        if (SelectionPolicy.ShouldSwitch(Snapshot(selection), userProfileId))
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
        var snapshot = Snapshot(selection);
        int? profileCount = SelectionPolicy.NeedsProfileCount(snapshot, assertedOwner, assertedVersion)
            ? await database.UserProfiles.AsNoTracking().CountAsync(cancellationToken) : null;
        SelectionPolicy.CheckAssertions(snapshot, assertedOwner, assertedVersion, profileCount);
        var owner = selection.UserProfileId.GetValueOrDefault();
        SelectionPolicy.CheckProtection(await database.Users.AsNoTracking()
            .AnyAsync(account => account.UserProfileId == owner, cancellationToken));
        return new LocalProfileBinding(owner, selection.Version);
    }

    private static void CheckVersion(LocalProfileSelection selection, Guid expectedVersion) =>
        SelectionPolicy.CheckVersion(Snapshot(selection), expectedVersion);

    public async Task<SelectionSnapshot> GetSelectionAsync(CancellationToken cancellationToken) =>
        Snapshot(await GetAsync(cancellationToken));

    public async Task<IReadOnlyList<ProfileIdentity>> ListProfilesAsync(CancellationToken cancellationToken) =>
        await database.UserProfiles.AsNoTracking().OrderBy(profile => profile.DisplayName).ThenBy(profile => profile.Id)
            .Select(profile => new ProfileIdentity(profile.Id, profile.DisplayName,
                database.Users.Any(account => account.UserProfileId == profile.Id)))
            .ToListAsync(cancellationToken);

    public async Task<SelectionSnapshot> SelectProfileAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken) =>
        Snapshot(await SelectAsync(userProfileId, expectedVersion, cancellationToken));

    private static SelectionSnapshot Snapshot(LocalProfileSelection selection) =>
        new(selection.UserProfileId, selection.Version, selection.LegacySingleProfileAccess);
}
