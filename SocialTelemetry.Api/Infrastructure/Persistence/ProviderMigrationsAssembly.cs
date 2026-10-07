using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

// Select standard EF artifacts by provider, without a second context/project or a migration runner.
public sealed class ProviderMigrationsAssembly : IMigrationsAssembly
{
    public const string PostgreSqlNamespace = "SocialTelemetry.Api.Infrastructure.Persistence.Migrations";
    public const string SqliteNamespace = PostgreSqlNamespace + ".Sqlite";
    private readonly IMigrationsIdGenerator idGenerator;
    private readonly string migrationNamespace;

    public ProviderMigrationsAssembly(ICurrentDbContext currentContext, IMigrationsIdGenerator idGenerator)
    {
        this.idGenerator = idGenerator;
        migrationNamespace = currentContext.Context.Database.ProviderName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => PostgreSqlNamespace,
            "Microsoft.EntityFrameworkCore.Sqlite" => SqliteNamespace,
            _ => throw new InvalidOperationException("Unsupported persistence provider.")
        };
        var types = Assembly.DefinedTypes.Where(type => !type.IsAbstract && type.Namespace == migrationNamespace &&
            type.GetCustomAttribute<DbContextAttribute>()?.ContextType == typeof(AppDbContext)).ToArray();
        Migrations = types.Where(type => type.IsSubclassOf(typeof(Migration)))
            .OrderBy(type => type.GetCustomAttribute<MigrationAttribute>()?.Id, StringComparer.Ordinal)
            .ToDictionary(type => type.GetCustomAttribute<MigrationAttribute>()?.Id
                ?? throw new InvalidOperationException("Migration identifier is missing."), type => type);
        var snapshot = types.SingleOrDefault(type => type.IsSubclassOf(typeof(ModelSnapshot)));
        ModelSnapshot = snapshot is null ? null : Activator.CreateInstance(snapshot.AsType()) as ModelSnapshot;
    }

    public Assembly Assembly => typeof(AppDbContext).Assembly;
    public IReadOnlyDictionary<string, TypeInfo> Migrations { get; }
    public ModelSnapshot? ModelSnapshot { get; }

    public string? FindMigrationId(string nameOrId) => Migrations.Keys.FirstOrDefault(id =>
        string.Equals(idGenerator.IsValidId(nameOrId) ? id : idGenerator.GetName(id), nameOrId, StringComparison.OrdinalIgnoreCase));

    public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        if (migrationClass.Namespace != migrationNamespace)
            throw new InvalidOperationException("Migration belongs to another provider.");
        var migration = Activator.CreateInstance(migrationClass.AsType()) as Migration
            ?? throw new InvalidOperationException("Could not create migration.");
        migration.ActiveProvider = activeProvider;
        return migration;
    }
}
