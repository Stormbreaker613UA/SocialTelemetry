using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed partial class LocalAccessManagement
{
    public async Task<AccessChange> RecoverAsync(RecoveryInput input, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(input.Login, 256);
        LocalAccessInput.Check(input.RecoveryCode, 64);
        LocalAccessInput.Check(input.NewPassword);
        runtime.TakeAuthenticationAttempt();
        LocalAccessFailure? failure = null;
        var result = await ChangeAsync(async selection =>
        {
            var normalizedLogin = users.NormalizeName(input.Login);
            var account = await LoadFreshAccountAsync(user => user.NormalizedUserName == normalizedLogin, cancellationToken);
            if (account is null || account.LocalProtection != LocalProtectionStatus.Enabled || account.RecoveryHash is null)
            {
                failure = LocalAccessFailure.InvalidCredentials;
                return new AccessChange(Snapshot(selection));
            }
            var now = clock.GetUtcNow();
            if (account.RecoveryLockoutEnd > now)
            {
                failure = LocalAccessFailure.LockedOut;
                return new AccessChange(Snapshot(selection));
            }
            var supplied = "LocalRecovery.v1:" + account.Id + ":" + input.RecoveryCode;
            if (users.PasswordHasher.VerifyHashedPassword(account, account.RecoveryHash, supplied) == PasswordVerificationResult.Failed)
            {
                account.RecoveryFailures++;
                if (account.RecoveryFailures >= options.Value.FailedAttempts)
                {
                    account.RecoveryFailures = 0;
                    account.RecoveryLockoutEnd = now.AddMinutes(options.Value.LockoutMinutes);
                    failure = LocalAccessFailure.LockedOut;
                }
                else failure = LocalAccessFailure.InvalidCredentials;
                return new AccessChange(Snapshot(selection));
            }
            // The framework reset token never leaves this transaction or reaches a client.
            var resetToken = await users.GeneratePasswordResetTokenAsync(account);
            CheckIdentity(await users.ResetPasswordAsync(account, resetToken, input.NewPassword));
            var replacement = ReplaceRecovery(account);
            await RevokeAccountAsync(account, selection, cancellationToken);
            return new AccessChange(Snapshot(selection), RecoveryCode: replacement);
        }, cancellationToken);
        if (failure is { } reason) throw new LocalAccessException(reason);
        return result;
    }
}
