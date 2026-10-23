using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed partial class LocalAccessManagement
{
    // Called only after selector/admission protection; no framework autosave/rehash may join deletion.
    internal async Task<LocalAccessFailure?> CheckDeletionPasswordAsync(Guid owner, string? password,
        CancellationToken cancellationToken)
    {
        var account = await LoadFreshAccountAsync(user => user.UserProfileId == owner, cancellationToken);
        // The caller refused pending writes before loading security state. Reconciling a
        // retained identity entry can mark its freshly read properties modified; a locked-out
        // deletion does not framework-save them. Do not leave that read as pending caller work.
        if (account is not null) database.Entry(account).State = EntityState.Unchanged;
        if (account is null || account.LocalProtection == LocalProtectionStatus.Disabled) return null;
        if (account.LocalProtection != LocalProtectionStatus.Enabled)
            throw new LocalProfileAccessException(LocalProfileFailure.ProtectedAccessUnavailable);
        LocalAccessInput.Check(password);
        runtime.TakeAuthenticationAttempt();
        if (account.LockoutEnd > clock.GetUtcNow()) return LocalAccessFailure.LockedOut;
        if (account.PasswordHash is null || users.PasswordHasher.VerifyHashedPassword(account, account.PasswordHash, password!) ==
            PasswordVerificationResult.Failed)
        {
            var failure = RecordPasswordFailure(account);
            using var securityMutation = database.BeginLocalAccessMutation();
            await database.SaveLocalAccessChangesAsync(cancellationToken);
            return failure;
        }
        return null;
    }
}
