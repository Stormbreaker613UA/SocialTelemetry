using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Observability;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class ProfileAvatarReconciliationService(IServiceScopeFactory scopeFactory,
    IOptions<StorageMaintenanceOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var operation = SocialTelemetryTelemetry.Start("storage.reconcile_avatars", cancellationToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars");
        var cleanup = scope.ServiceProvider.GetRequiredService<ProfileAvatarCleanup>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ProfileAvatarReconciliationService>>();
        try
        {
            var keys = await database.UserProfiles.AsNoTracking().Where(profile => profile.AvatarStorageKey != null)
                .Select(profile => profile.AvatarStorageKey!).Concat(database.People.AsNoTracking()
                    .Where(person => person.AvatarStorageKey != null).Select(person => person.AvatarStorageKey!))
                .ToListAsync(cancellationToken);
            foreach (var key in keys.Distinct())
            {
                SocialTelemetryTelemetry.RecordReconciliation("avatars", "inspected");
                if (!await storage.ExistsAsync(key, cancellationToken))
                {
                    SocialTelemetryTelemetry.RecordReconciliation("avatars", "missing");
                    logger.LogWarning("Avatar {StorageKey} is missing; its profile reference remains unchanged", key);
                }
            }

            var cutoff = DateTimeOffset.UtcNow - options.Value.OrphanSafetyAge;
            var orphans = storage.EnumerateFiles().Concat(storage.EnumerateStagedFiles())
                .Where(file => file.LastModified < cutoff).Select(file => file.StorageKey).ToArray();
            await cleanup.DeleteUnreferencedAsync(database, orphans);
            operation.Complete();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            operation.Fail(exception);
            SocialTelemetryTelemetry.RecordReconciliation("avatars", "failed");
            logger.LogWarning("Avatar reconciliation deferred ({ExceptionType})", exception.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
