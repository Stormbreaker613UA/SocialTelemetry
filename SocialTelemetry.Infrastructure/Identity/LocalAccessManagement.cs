using System.Security.Cryptography;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;
using SocialTelemetry.Application.Features.LocalProfiles;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed partial class LocalAccessManagement(AppDbContext database, UserManager<ApplicationUser> users,
    LocalProfileOperation operation, LocalAccessRequest request, LocalAccessValidation validation,
    LocalAccessRuntime runtime, LocalProfileLifetimes lifetimes, TimeProvider clock, IOptions<LocalAccessOptions> options,
    LocalProfileSelectionService profileSelector)
    : ILocalAccessManagement
{
    public async Task<AccessState> StatusAsync(CancellationToken cancellationToken)
    {
        var selection = await profileSelector.GetAsync(cancellationToken);
        var account = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.UserProfileId == selection.UserProfileId, cancellationToken);
        LocalAccessBinding? access = null;
        try
        {
            if (selection.UserProfileId is { } owner) access = await validation.FindAsync(owner, request.Evidence, cancellationToken);
        }
        catch (LocalAccessException) { }
        catch (LocalProfileAccessException) { }
        var grant = access is null ? null : await database.Set<LocalProfileGrant>().AsNoTracking().SingleAsync(row => row.Id == access.GrantId, cancellationToken);
        var isProtected = account is not null && account.LocalProtection != LocalProtectionStatus.Disabled;
        return new(selection.UserProfileId, selection.Version, isProtected,
            isProtected && (grant is null || selection.AccessGrantId != grant.Id), grant?.Remembered ?? false, grant?.ExpiresAt);
    }

    public async Task<AccessChange> EnableAsync(ProtectionEnrollment input, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(input.Login, 256);
        LocalAccessInput.Check(input.Password);
        runtime.TakeAuthenticationAttempt();
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        return await ChangeAsync(async selection =>
        {
            await validation.ValidateAsync(operation.Binding!, cancellationToken);
            var account = await LoadFreshAccountAsync(user => user.UserProfileId == owner, cancellationToken);
            if (account is not null && account.LocalProtection != LocalProtectionStatus.Disabled)
                throw new LocalAccessException(LocalAccessFailure.SecurityChanged);
            if (account is null)
            {
                account = new ApplicationUser { UserProfileId = owner, UserName = input.Login };
                CheckIdentity(await users.CreateAsync(account, input.Password));
            }
            else
            {
                account.UserName = input.Login;
                CheckIdentity(await users.AddPasswordAsync(account, input.Password));
            }
            account.LocalProtection = LocalProtectionStatus.Enabled;
            var code = ReplaceRecovery(account);
            await RevokeAccountAsync(account, selection, cancellationToken);
            return new AccessChange(Snapshot(selection), RecoveryCode: code);
        }, cancellationToken);
    }

    public async Task<AccessChange> SignInAsync(PasswordSignIn input, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(input.Login, 256);
        LocalAccessInput.Check(input.Password);
        runtime.TakeAuthenticationAttempt();
        // Read/verify under the same short selector transaction as failure accounting and activation.
        LocalAccessFailure? failure = null;
        var result = await ChangeAsync(async selection =>
        {
            SelectionPolicy.CheckVersion(Snapshot(selection), input.ExpectedVersion);
            var normalizedLogin = users.NormalizeName(input.Login);
            var account = await LoadFreshAccountAsync(user => user.NormalizedUserName == normalizedLogin, cancellationToken);
            if (account is null || account.UserProfileId != input.UserProfileId || account.LocalProtection != LocalProtectionStatus.Enabled)
            {
                failure = LocalAccessFailure.InvalidCredentials;
                return new AccessChange(Snapshot(selection));
            }
            if (account.LockoutEnd > clock.GetUtcNow())
            {
                failure = LocalAccessFailure.LockedOut;
                return new AccessChange(Snapshot(selection));
            }
            if (!await users.CheckPasswordAsync(account, input.Password))
            {
                failure = RecordPasswordFailure(account);
                return new AccessChange(Snapshot(selection));
            }
            account.AccessFailedCount = 0;
            account.LockoutEnd = null;
            var evidence = request.Evidence.DeviceToken is { Length: 64 } ? request.Evidence : new LocalAccessEvidence(NewSecret());
            var hash = LocalAccessValidation.TokenHash(evidence.DeviceToken!);
            var device = await database.Set<LocalDeviceTicket>().SingleOrDefaultAsync(row => row.TokenHash == hash, cancellationToken);
            if (device is null)
            {
                device = new LocalDeviceTicket { TokenHash = hash };
                database.Add(device);
            }
            await database.Set<LocalProfileGrant>().Where(grant => grant.DeviceId == device.Id && grant.AccountId == account.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.Revoked, true), cancellationToken);
            var now = clock.GetUtcNow();
            var grant = new LocalProfileGrant
            {
                DeviceId = device.Id, AccountId = account.Id, SecurityEpoch = account.AccessEpoch,
                SecurityStamp = account.SecurityStamp!, HostInstance = runtime.HostInstance, Remembered = input.Remember,
                SignedInAt = now, LastActivityAt = now,
                ExpiresAt = input.Remember ? now.AddDays(options.Value.RememberDays) : now.AddHours(options.Value.AbsoluteHours)
            };
            database.Add(grant);
            if (grant.ExpiresAt > device.ExpiresAt) device.ExpiresAt = grant.ExpiresAt;
            await ActivateAsync(selection, account.UserProfileId, grant.Id, cancellationToken);
            var retainedRemembered = await database.Set<LocalProfileGrant>().AnyAsync(current => current.DeviceId == device.Id &&
                current.Remembered && !current.Revoked && current.ExpiresAt > now, cancellationToken);
            return new AccessChange(Snapshot(selection), evidence)
            {
                PersistentDevice = input.Remember || retainedRemembered, DeviceExpiresAt = device.ExpiresAt
            };
        }, cancellationToken);
        if (failure is { } reason) throw new LocalAccessException(reason);
        return result;
    }

    public Task<AccessChange> ResumeAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken) =>
        ChangeAsync(async selection =>
        {
            SelectionPolicy.CheckVersion(Snapshot(selection), expectedVersion);
            var access = await validation.FindAsync(userProfileId, request.Evidence, cancellationToken)
                ?? throw new LocalAccessException(LocalAccessFailure.Required);
            var grant = await database.Set<LocalProfileGrant>().SingleAsync(row => row.Id == access.GrantId, cancellationToken);
            if (!grant.Remembered) throw new LocalAccessException(LocalAccessFailure.Required);
            await ActivateAsync(selection, userProfileId, grant.Id, cancellationToken);
            return new AccessChange(Snapshot(selection));
        }, cancellationToken);

    public Task<AccessChange> ChangePasswordAsync(PasswordChange input, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(input.NewPassword);
        return WithPasswordAsync(input.CurrentPassword, async (account, selection) =>
        {
            CheckIdentity(await users.ChangePasswordAsync(account, input.CurrentPassword, input.NewPassword));
            await RevokeAccountAsync(account, selection, cancellationToken);
            return new AccessChange(Snapshot(selection));
        }, cancellationToken);
    }

    public Task<AccessChange> DisableAsync(string password, CancellationToken cancellationToken) =>
        WithPasswordAsync(password, async (account, selection) =>
        {
            CheckIdentity(await users.RemovePasswordAsync(account));
            account.LocalProtection = LocalProtectionStatus.Disabled;
            account.RecoveryHash = null;
            await RevokeAccountAsync(account, selection, cancellationToken);
            return new AccessChange(Snapshot(selection));
        }, cancellationToken);

    public Task<AccessChange> RotateRecoveryAsync(string password, CancellationToken cancellationToken) =>
        WithPasswordAsync(password, async (account, selection) =>
        {
            var code = ReplaceRecovery(account);
            await RevokeAccountAsync(account, selection, cancellationToken);
            return new AccessChange(Snapshot(selection), RecoveryCode: code);
        }, cancellationToken);

    public Task<AccessChange> LockAsync(Guid userProfileId, CancellationToken cancellationToken) =>
        ChangeAsync(async selection =>
        {
            if (request.Evidence.DeviceToken is not { Length: 64 } token)
                throw new LocalAccessException(LocalAccessFailure.Required);
            var hash = LocalAccessValidation.TokenHash(token);
            var device = await database.Set<LocalDeviceTicket>().SingleOrDefaultAsync(row => row.TokenHash == hash, cancellationToken)
                ?? throw new LocalAccessException(LocalAccessFailure.Required);
            var account = await database.Users.SingleOrDefaultAsync(user => user.UserProfileId == userProfileId, cancellationToken);
            if (account is not null)
            {
                var affected = await database.Set<LocalProfileGrant>().Where(grant => grant.DeviceId == device.Id && grant.AccountId == account.Id)
                    .Select(grant => grant.Id).ToListAsync(cancellationToken);
                await database.Set<LocalProfileGrant>().Where(grant => grant.DeviceId == device.Id && grant.AccountId == account.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.Revoked, true), cancellationToken);
                if (selection.AccessGrantId is { } current && affected.Contains(current)) Close(selection);
            }
            return new AccessChange(Snapshot(selection));
        }, cancellationToken);

    public async Task ActivityAsync(CancellationToken cancellationToken)
    {
        using var admission = await operation.EnterAsync(cancellationToken);
        await ChangeAsync(async selection =>
        {
            await validation.ValidateAsync(operation.Binding!, cancellationToken);
            if (operation.Binding?.Access is { } access)
            {
                var grant = await database.Set<LocalProfileGrant>().SingleAsync(row => row.Id == access.GrantId, cancellationToken);
                grant.LastActivityAt = clock.GetUtcNow();
            }
            return new AccessChange(Snapshot(selection));
        }, cancellationToken);
    }

    private async Task<AccessChange> WithPasswordAsync(string password,
        Func<ApplicationUser, LocalProfileSelection, Task<AccessChange>> change, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(password);
        runtime.TakeAuthenticationAttempt();
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        LocalAccessFailure? failure = null;
        var result = await ChangeAsync(async selection =>
        {
            await validation.ValidateAsync(operation.Binding!, cancellationToken);
            var account = await LoadFreshAccountAsync(user => user.UserProfileId == owner, cancellationToken)
                ?? throw new LocalAccessException(LocalAccessFailure.SecurityChanged);
            if (account.LockoutEnd > clock.GetUtcNow()) failure = LocalAccessFailure.LockedOut;
            else if (!await users.CheckPasswordAsync(account, password))
            {
                failure = RecordPasswordFailure(account);
            }
            if (failure is not null) return new AccessChange(Snapshot(selection));
            account.AccessFailedCount = 0;
            account.LockoutEnd = null;
            return await change(account, selection);
        }, cancellationToken);
        if (failure is { } reason) throw new LocalAccessException(reason);
        return result;
    }

    private async Task<AccessChange> ChangeAsync(Func<LocalProfileSelection, Task<AccessChange>> change, CancellationToken cancellationToken)
    {
        await lifetimes.SelectionGate.WaitAsync(cancellationToken);
        LocalProfileSelection selection;
        AccessChange result;
        CancellationTokenSource? retired = null;
        var securityMutationStarted = false;
        try
        {
            if (database.ChangeTracker.Entries().Any(entry => IsSecurityEntity(entry.Entity) && entry.State != EntityState.Unchanged))
                throw new InvalidOperationException("Local access requires no pending security changes.");
            securityMutationStarted = true;
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.LockLocalProfileSelectionAsync(cancellationToken);
            using var securityMutation = database.BeginLocalAccessMutation();
            selection = await database.Set<LocalProfileSelection>().SingleAsync(cancellationToken);
            result = await change(selection);
            // Security operations intentionally invalidate their own admission. Framework stores
            // use the same context/transaction; only this explicit security scope may save here.
            await database.SaveLocalAccessChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            retired = lifetimes.Replace(selection.Version);
        }
        catch
        {
            // Identity may have accepted tracked values before the enclosing SQL commit
            // failed. A reused scope must reload durable security state, not those values.
            if (securityMutationStarted)
            {
                foreach (var entry in database.ChangeTracker.Entries().Where(entry => IsSecurityEntity(entry.Entity)).ToArray())
                    entry.State = EntityState.Detached;
            }
            throw;
        }
        finally
        {
            lifetimes.SelectionGate.Release();
            lifetimes.CancelRetired(retired);
        }
        return result with { Selection = Snapshot(selection) };
    }

    private async Task<ApplicationUser?> LoadFreshAccountAsync(Expression<Func<ApplicationUser, bool>> predicate,
        CancellationToken cancellationToken)
    {
        // The selector serializes writers, but EF's identity map can still contain a
        // successfully committed earlier failure. Read durable state before using Identity.
        var fresh = await database.Users.AsNoTracking().SingleOrDefaultAsync(predicate, cancellationToken);
        if (fresh is null) return null;
        var tracked = database.Users.Local.SingleOrDefault(account => account.Id == fresh.Id);
        if (tracked is null)
        {
            database.Attach(fresh);
            return fresh;
        }
        var entry = database.Entry(tracked);
        entry.CurrentValues.SetValues(fresh);
        entry.OriginalValues.SetValues(fresh);
        return tracked;
    }

    private static bool IsSecurityEntity(object entity) =>
        entity is ApplicationUser or LocalProfileSelection or LocalDeviceTicket or LocalProfileGrant;

    private async Task ActivateAsync(LocalProfileSelection selection, Guid owner, Guid grantId, CancellationToken cancellationToken)
    {
        if (selection.AccessGrantId is { } old && old != grantId)
            await database.Set<LocalProfileGrant>().Where(grant => grant.Id == old && !grant.Remembered)
                .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.Revoked, true), cancellationToken);
        if (selection.UserProfileId != owner || selection.AccessGrantId != grantId)
        {
            selection.UserProfileId = owner;
            selection.AccessGrantId = grantId;
            selection.LegacySingleProfileAccess = false;
            selection.Version = Guid.NewGuid();
        }
    }

    private async Task RevokeAccountAsync(ApplicationUser account, LocalProfileSelection selection, CancellationToken cancellationToken)
    {
        account.AccessEpoch = Guid.NewGuid();
        CheckIdentity(await users.UpdateSecurityStampAsync(account));
        await database.Set<LocalProfileGrant>().Where(grant => grant.AccountId == account.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.Revoked, true), cancellationToken);
        if (selection.UserProfileId == account.UserProfileId) Close(selection);
    }

    private string ReplaceRecovery(ApplicationUser account)
    {
        var code = NewSecret();
        account.RecoveryHash = users.PasswordHasher.HashPassword(account, "LocalRecovery.v1:" + account.Id + ":" + code);
        account.RecoveryFailures = 0;
        account.RecoveryLockoutEnd = null;
        return code;
    }

    private LocalAccessFailure RecordPasswordFailure(ApplicationUser account)
    {
        account.AccessFailedCount++;
        if (account.AccessFailedCount < options.Value.FailedAttempts) return LocalAccessFailure.InvalidCredentials;
        account.AccessFailedCount = 0;
        account.LockoutEnd = clock.GetUtcNow().AddMinutes(options.Value.LockoutMinutes);
        return LocalAccessFailure.LockedOut;
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static void CheckIdentity(IdentityResult result)
    {
        if (result.Succeeded) return;
        if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
            throw new LocalAccessException(LocalAccessFailure.SecurityChanged);
        throw new LocalAccessException(LocalAccessFailure.InvalidInput);
    }
    private static void Close(LocalProfileSelection selection)
    {
        selection.AccessGrantId = null;
        selection.Version = Guid.NewGuid();
    }
    private static SelectionSnapshot Snapshot(LocalProfileSelection selection) => new(selection.UserProfileId, selection.Version, selection.LegacySingleProfileAccess);
}
