using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.Runtime;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PersistenceOptions>().BindConfiguration("Persistence")
            .Validate(options => options.HasValidSqliteTimeout(), "Persistence:SqliteTimeoutSeconds must be between 1 and 120 for Sqlite.")
            .Validate(options => options.Provider is "PostgreSql" or "Sqlite", "Persistence:Provider must be PostgreSql or Sqlite.")
            .Validate(options => options.Provider != "PostgreSql" || !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Default")),
                "PostgreSql requires ConnectionStrings:Default.")
            .Validate<ApplicationPaths>((options, paths) => options.Provider != "Sqlite" || options.HasValidSqlitePath(paths),
                "Persistence:SqliteFile must name a valid local database file.")
            .ValidateOnStart();
        services.AddDbContext<AppDbContext>((provider, database) =>
            Configure(database, provider.GetRequiredService<IOptions<PersistenceOptions>>().Value,
                provider.GetRequiredService<ApplicationPaths>(), configuration.GetConnectionString("Default")));
        // Run before storage reconciliation, which queries the schema at startup.
        services.AddHostedService<SqliteDatabaseInitialization>();
        return services;
    }

    public static void Configure(DbContextOptionsBuilder database, PersistenceOptions options,
        ApplicationPaths paths, string? postgresConnection)
    {
        if (!options.HasValidSqliteTimeout())
            throw new InvalidOperationException("Persistence:SqliteTimeoutSeconds must be between 1 and 120 for Sqlite.");
        switch (options.Provider)
        {
            case "PostgreSql":
                if (string.IsNullOrWhiteSpace(postgresConnection))
                    throw new InvalidOperationException("PostgreSql requires ConnectionStrings:Default.");
                database.UseNpgsql(postgresConnection, postgres => postgres.ConfigureDataSource(source => source.ConfigureTracing(tracing => tracing
                    .ConfigureCommandSpanNameProvider(_ => "database.command")
                    .ConfigureBatchSpanNameProvider(_ => "database.command")
                    .ConfigureCommandEnrichmentCallback((activity, _) => activity.SetTag("db.query.text", null))
                    .ConfigureBatchEnrichmentCallback((activity, _) => activity.SetTag("db.query.text", null))
                    .EnablePhysicalOpenTracing(false).EnableFirstResponseEvent(false))));
                break;
            case "Sqlite":
                if (!options.HasValidSqlitePath(paths))
                    throw new InvalidOperationException("Persistence:SqliteFile must name a valid local database file.");
                var file = paths.DatabaseFile(options.SqliteFile);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)
                        ?? throw new InvalidOperationException("SQLite requires a database directory."));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("SQLite database directory is not accessible.");
                }
                database.UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = file, ForeignKeys = true, Pooling = false,
                    DefaultTimeout = options.SqliteTimeoutSeconds, Cache = SqliteCacheMode.Private
                }.ToString());
                break;
            default:
                throw new InvalidOperationException("Persistence:Provider must be PostgreSql or Sqlite.");
        }
    }
}
