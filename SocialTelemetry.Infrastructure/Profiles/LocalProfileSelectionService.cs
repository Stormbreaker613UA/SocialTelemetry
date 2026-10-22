using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.LocalProfiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Infrastructure.Profiles;

public sealed class LocalProfileSelectionService(AppDbContext database, LocalProfileLifetimes lifetimes,
    LocalAccessRequest? accessRequest = null, Identity.LocalAccessValidation? accessValidation = null) : ILocalProfileManagement
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
        CancellationTokenSource? retired = null;
        try
        {
            var selected = await SelectCoreAsync(userProfileId, expectedVersion, cancellationToken);
            retired = lifetimes.Replace(selected.Version);
            return selected;
        }
        finally { lifetimes.SelectionGate.Release(); lifetimes.CancelRetired(retired); }
    }

    private async Task<LocalProfileSelection> SelectCoreAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockLocalProfileSelectionAsync(cancellationToken);
        var selection = await database.Set<LocalProfileSelection>().SingleAsync(cancellationToken);
        CheckVersion(selection, expectedVersion);
        SelectionPolicy.CheckTargetExists(await database.UserProfiles.AnyAsync(profile => profile.Id == userProfileId, cancellationToken));
        // Selection is not authentication: protected targets require their own device grant.
        var access = await FindAccessAsync(userProfileId, cancellationToken);
        if (SelectionPolicy.ShouldSwitch(Snapshot(selection), userProfileId) || selection.AccessGrantId != access?.GrantId)
        {
            if (selection.AccessGrantId is { } previousId)
                await database.Set<Identity.LocalProfileGrant>().Where(grant => grant.Id == previousId && !grant.Remembered)
                    .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.Revoked, true), cancellationToken);
            selection.UserProfileId = userProfileId;
            selection.AccessGrantId = access?.GrantId;
            selection.Version = Guid.NewGuid();
            selection.LegacySingleProfileAccess = false;
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return selection;
    }

    public async Task<LocalProfileBinding> ResolveAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken)
    {
        // Refresh a cooperating host's committed selection without allowing an older local
        // resolver to publish after a newer local switch. SQL remains the final commit guard.
        await lifetimes.SelectionGate.WaitAsync(cancellationToken);
        CancellationTokenSource? retired = null;
        try
        {
            var binding = await ResolveCoreAsync(assertedOwner, assertedVersion, cancellationToken);
            retired = lifetimes.Replace(binding.SelectionVersion);
            return binding;
        }
        finally { lifetimes.SelectionGate.Release(); lifetimes.CancelRetired(retired); }
    }

    private async Task<LocalProfileBinding> ResolveCoreAsync(Guid? assertedOwner, Guid? assertedVersion, CancellationToken cancellationToken)
    {
        var selection = await GetAsync(cancellationToken);
        var snapshot = Snapshot(selection);
        int? profileCount = SelectionPolicy.NeedsProfileCount(snapshot, assertedOwner, assertedVersion)
            ? await database.UserProfiles.AsNoTracking().CountAsync(cancellationToken) : null;
        SelectionPolicy.CheckAssertions(snapshot, assertedOwner, assertedVersion, profileCount);
        var owner = selection.UserProfileId.GetValueOrDefault();
        var access = await FindAccessAsync(owner, cancellationToken);
        if (selection.AccessGrantId != access?.GrantId) throw new LocalAccessException(LocalAccessFailure.Required);
        return new LocalProfileBinding(owner, selection.Version) { Access = access };
    }

    private async Task<LocalAccessBinding?> FindAccessAsync(Guid owner, CancellationToken cancellationToken)
    {
        if (accessValidation is not null)
            return await accessValidation.FindAsync(owner, accessRequest?.Evidence ?? new(null), cancellationToken);
        SelectionPolicy.CheckProtection(await database.Users.AsNoTracking().AnyAsync(account => account.UserProfileId == owner, cancellationToken));
        return null;
    }

    private static void CheckVersion(LocalProfileSelection selection, Guid expectedVersion) =>
        SelectionPolicy.CheckVersion(Snapshot(selection), expectedVersion);

    public async Task<SelectionSnapshot> GetSelectionAsync(CancellationToken cancellationToken) =>
        Snapshot(await GetAsync(cancellationToken));

    public async Task<IReadOnlyList<ProfileIdentity>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        var rows = await database.UserProfiles.AsNoTracking().OrderBy(profile => profile.DisplayName).ThenBy(profile => profile.Id)
            .Select(profile => new
            {
                profile.Id, profile.DisplayName,
                Protection = database.Users.Where(account => account.UserProfileId == profile.Id)
                    .Select(account => (Identity.LocalProtectionStatus?)account.LocalProtection).SingleOrDefault()
            }).ToListAsync(cancellationToken);
        var profiles = new List<ProfileIdentity>();
        foreach (var row in rows)
        {
            var state = "Unprotected";
            if (row.Protection == Identity.LocalProtectionStatus.Enabled) state = "Protected";
            else if (row.Protection is not null && row.Protection != Identity.LocalProtectionStatus.Disabled) state = "Unavailable";
            profiles.Add(new ProfileIdentity(row.Id, row.DisplayName, row.Protection is not null) { Protection = state });
        }
        return profiles;
    }

    public async Task<SelectionSnapshot> SelectProfileAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken) =>
        Snapshot(await SelectAsync(userProfileId, expectedVersion, cancellationToken));

    private static SelectionSnapshot Snapshot(LocalProfileSelection selection) =>
        new(selection.UserProfileId, selection.Version, selection.LegacySingleProfileAccess);
}
