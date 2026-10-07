using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Observability;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitialization(IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!database.Database.IsSqlite()) return;
        using var operation = SocialTelemetryTelemetry.Start("database.migrate", cancellationToken);
        operation.Activity?.SetTag("db.system.name", "sqlite");
        try
        {
            await database.Database.MigrateAsync(cancellationToken);
            operation.Complete();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            operation.Fail(exception);
            // Startup failures must not print a database path or provider error containing user data.
            throw new InvalidOperationException("SQLite initialization failed. Check database access and migration state.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
