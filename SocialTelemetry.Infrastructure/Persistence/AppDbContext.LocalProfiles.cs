using SocialTelemetry.Application.Contracts.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Profiles;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed partial class AppDbContext
{
    public async Task LockLocalProfileSelectionAsync(CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("Local selection protection requires a transaction.");
        var initialVersion = Guid.NewGuid();
        await Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LocalProfileSelection" ("Id", "UserProfileId", "Version", "LegacySingleProfileAccess")
            VALUES (1, NULL, {initialVersion}, {false})
            ON CONFLICT ("Id") DO UPDATE SET "Id" = EXCLUDED."Id"
            """, cancellationToken);
    }

    private async Task ProtectLocalProfileOperationAsync(CancellationToken cancellationToken)
    {
        if (profileOperation?.Binding is not { } binding) return;
        profileOperation.Lifetime.ThrowIfCancellationRequested();
        // Lock order: local selector -> original profile guard -> credential execution lease.
        // This guard is held only by short commit transactions, never across inference.
        var affected = await Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "LocalProfileSelection" SET "Id" = "Id"
            WHERE "Id" = 1 AND "UserProfileId" = {binding.UserProfileId} AND "Version" = {binding.SelectionVersion}
            """, cancellationToken);
        if (affected != 1) throw new LocalProfileAccessException(LocalProfileFailure.SelectionChanged);
        if (profileOperation is not null)
        {
            using var admission = await profileOperation.EnterAsync(cancellationToken);
        }
    }
}
