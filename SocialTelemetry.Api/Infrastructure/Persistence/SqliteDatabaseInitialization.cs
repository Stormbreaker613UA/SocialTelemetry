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
            // Existing empty/DELETE-mode files skip EF's database creation path. Establish WAL explicitly.
            await database.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = database.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL";
                var mode = await command.ExecuteScalarAsync(cancellationToken);
                if (!string.Equals(mode as string, "wal", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SQLite requires WAL journal mode.");
                command.CommandText = "PRAGMA synchronous=FULL";
                await command.ExecuteNonQueryAsync(cancellationToken);
                command.CommandText = "PRAGMA synchronous";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 2)
                    throw new InvalidOperationException("SQLite requires full durability.");
            }
            finally
            {
                await database.Database.CloseConnectionAsync();
            }
            await database.Database.MigrateAsync(cancellationToken);
            operation.Complete();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            operation.Fail(exception);
            // Startup failures must not print a database path or provider error containing user data.
            throw new InvalidOperationException("SQLite initialization failed. Check database access, WAL support and migration state.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
