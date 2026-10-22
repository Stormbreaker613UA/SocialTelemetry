using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Features.LocalAccess;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed partial class LocalAccessManagement
{
    internal async Task ExpireAsync(CancellationToken cancellationToken)
    {
        // Do not acquire a database writer lock for polling a valid/unprotected profile.
        // Serialize this authoritative read/publication exactly like selection resolution.
        await lifetimes.SelectionGate.WaitAsync(cancellationToken);
        CancellationTokenSource? retired = null;
        var expired = false;
        try
        {
            var selection = await database.Set<Profiles.LocalProfileSelection>().AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (selection is null) return;
            retired = lifetimes.Replace(selection.Version);
            if (selection.AccessGrantId is { } id)
            {
                var grant = await database.Set<LocalProfileGrant>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                var account = grant is null ? null : await database.Users.AsNoTracking().SingleOrDefaultAsync(row => row.Id == grant.AccountId, cancellationToken);
                expired = grant is null || account is null || account.UserProfileId != selection.UserProfileId || runtime.IsExpired(grant, account);
            }
        }
        finally { lifetimes.SelectionGate.Release(); lifetimes.CancelRetired(retired); }
        if (expired) await ExpireProtectedAsync(cancellationToken);
    }

    private Task<AccessChange> ExpireProtectedAsync(CancellationToken cancellationToken) => ChangeAsync(async selection =>
    {
        if (selection.AccessGrantId is { } id)
        {
            var grant = await database.Set<LocalProfileGrant>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
            var account = grant is null ? null : await database.Users.AsNoTracking().SingleOrDefaultAsync(row => row.Id == grant.AccountId, cancellationToken);
            if (grant is null || account is null || account.UserProfileId != selection.UserProfileId || runtime.IsExpired(grant, account))
            {
                if (grant is not null) grant.Revoked = true;
                Close(selection);
            }
        }
        return new AccessChange(Snapshot(selection));
    }, cancellationToken);
}
