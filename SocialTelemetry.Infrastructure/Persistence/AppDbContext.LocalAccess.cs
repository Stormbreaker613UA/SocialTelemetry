using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Profiles;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed partial class AppDbContext
{
    private bool localAccessMutation;

    internal IDisposable BeginLocalAccessMutation()
    {
        if (Database.CurrentTransaction is null || localAccessMutation)
            throw new InvalidOperationException("Local access changes require one protected transaction.");
        localAccessMutation = true;
        return new LocalAccessMutation(this);
    }

    internal Task<int> SaveLocalAccessChangesAsync(CancellationToken cancellationToken) => SaveChangesAsync(cancellationToken);

    private Task RecordCommittedActivityAsync(CancellationToken cancellationToken)
    {
        if (profileOperation is not { UserMutation: true, Binding.Access: { } access }) return Task.CompletedTask;
        // The request's neutral admission port records starts separately. Successful ordinary
        // writes update activity in their own commit, so failed writes cannot keep a session alive.
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return Set<LocalProfileGrant>().Where(grant => grant.Id == access.GrantId)
            .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.LastActivityAt, now), cancellationToken);
    }

    private bool HasAccountSecurityChanges() => ChangeTracker.Entries<ApplicationUser>().Any(entry =>
        entry.State is EntityState.Added or EntityState.Deleted || entry.State == EntityState.Modified &&
        new[] { nameof(ApplicationUser.UserProfileId), nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.SecurityStamp),
            nameof(ApplicationUser.LocalProtection), nameof(ApplicationUser.AccessEpoch), nameof(ApplicationUser.RecoveryHash) }
            .Any(name => entry.Property(name).IsModified));

    private async Task<int> SaveAccountSecurityChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        if (AnalysisSourceChanges().Length > 0 || OrganizationChanges().Length > 0)
            throw new InvalidOperationException("Account security and journal mutations require separate operations.");
        var ownedTransaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await LockLocalProfileSelectionAsync(cancellationToken);
            var selection = await Set<LocalProfileSelection>().SingleAsync(cancellationToken);
            foreach (var entry in ChangeTracker.Entries<ApplicationUser>().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                entry.Entity.AccessEpoch = Guid.NewGuid();
                await Set<LocalProfileGrant>().Where(grant => grant.AccountId == entry.Entity.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.Revoked, true), cancellationToken);
                var previousOwner = entry.Property(account => account.UserProfileId).OriginalValue;
                if (selection.UserProfileId == previousOwner || selection.UserProfileId == entry.Entity.UserProfileId)
                {
                    selection.AccessGrantId = null;
                    selection.Version = Guid.NewGuid();
                }
            }
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
            return result;
        }
        finally { if (ownedTransaction is not null) await ownedTransaction.DisposeAsync(); }
    }

    private Task<int> SaveSecurityChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        // This narrow scope is not a generic save bypass: no journal mutation may join it.
        if (ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
            entry.Entity is not (ApplicationUser or LocalProfileSelection or LocalDeviceTicket or LocalProfileGrant)))
            throw new InvalidOperationException("Local access cannot persist journal changes.");
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private sealed class LocalAccessMutation(AppDbContext database) : IDisposable
    {
        public void Dispose() => database.localAccessMutation = false;
    }
}
