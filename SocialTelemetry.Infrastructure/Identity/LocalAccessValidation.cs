using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed class LocalAccessValidation(AppDbContext database, LocalAccessRuntime runtime, TimeProvider clock)
{
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<LocalAccessBinding?> FindAsync(Guid owner, LocalAccessEvidence evidence, CancellationToken cancellationToken)
    {
        var account = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.UserProfileId == owner, cancellationToken);
        if (account is null || account.LocalProtection == LocalProtectionStatus.Disabled) return null;
        if (account.LocalProtection != LocalProtectionStatus.Enabled)
            throw new LocalProfileAccessException(LocalProfileFailure.ProtectedAccessUnavailable);
        if (evidence.DeviceToken is not { Length: 64 } token) throw new LocalAccessException(LocalAccessFailure.Required);
        var hash = TokenHash(token);
        var device = await database.Set<LocalDeviceTicket>().AsNoTracking().SingleOrDefaultAsync(ticket => ticket.TokenHash == hash, cancellationToken);
        if (device is null) throw new LocalAccessException(LocalAccessFailure.Required);
        var grants = await database.Set<LocalProfileGrant>().AsNoTracking().Where(grant =>
            grant.DeviceId == device.Id && grant.AccountId == account.Id && !grant.Revoked).ToListAsync(cancellationToken);
        var valid = grants.SingleOrDefault(grant => runtime.IsValid(grant, account));
        if (valid is null) throw new LocalAccessException(LocalAccessFailure.Required);
        return new LocalAccessBinding(valid.Id, account.Id, account.AccessEpoch, evidence);
    }

    public async Task ValidateAsync(LocalProfileBinding binding, CancellationToken cancellationToken)
    {
        var selection = await database.Set<LocalProfileSelection>().AsNoTracking().SingleAsync(cancellationToken);
        if (selection.UserProfileId != binding.UserProfileId || selection.Version != binding.SelectionVersion)
            throw new LocalProfileAccessException(LocalProfileFailure.SelectionChanged);
        var actual = await FindAsync(binding.UserProfileId, binding.Access?.Evidence ?? new(null), cancellationToken);
        if (actual != binding.Access || selection.AccessGrantId != actual?.GrantId)
            throw new LocalAccessException(LocalAccessFailure.SecurityChanged);
    }

    public async Task RecordActivityAsync(LocalProfileBinding binding, CancellationToken cancellationToken)
    {
        if (binding.Access is null) return;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockLocalProfileSelectionAsync(cancellationToken);
        await ValidateAsync(binding, cancellationToken);
        var now = clock.GetUtcNow();
        await database.Set<LocalProfileGrant>().Where(grant => grant.Id == binding.Access.GrantId)
            .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.LastActivityAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
