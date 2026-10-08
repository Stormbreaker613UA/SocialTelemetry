using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Infrastructure.Observability;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Infrastructure.Persistence;

[Collection("SQLite")]
public sealed class SqlitePersistenceTests(SqliteApiFixture fixture) : IClassFixture<SqliteApiFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Startup_migrates_only_sqlite_and_retains_data_across_restart()
    {
        var profileId = await fixture.CreateUserProfileAsync("Persisted across hosts");
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(database.Database.IsSqlite());
            Assert.Contains(await database.Database.GetAppliedMigrationsAsync(Cancellation), migration => migration.EndsWith("InitialSqlite"));
            Assert.Empty(await database.Database.GetPendingMigrationsAsync(Cancellation));
            Assert.False(database.Database.HasPendingModelChanges());
            var migrations = database.GetService<IMigrationsAssembly>();
            Assert.Equal(ProviderMigrationsAssembly.SqliteNamespace, migrations.ModelSnapshot?.GetType().Namespace);
            Assert.Equal(2, migrations.Migrations.Count);
        }
        using var restarted = fixture.WithServices(_ => { });
        using var client = restarted.CreateClient();
        await using var newScope = restarted.Services.CreateAsyncScope();
        var reloaded = newScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Persisted across hosts", (await reloaded.UserProfiles.SingleAsync(profile => profile.Id == profileId, Cancellation)).DisplayName);
        Assert.True(File.Exists(Path.Combine(fixture.AttachmentStorageDirectory, "..", "socialtelemetry.db")));
    }

    [Fact]
    public async Task Foreign_keys_wal_full_durability_and_private_cache_are_enabled_on_each_connection()
    {
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectionString = new SqliteConnectionStringBuilder(database.Database.GetConnectionString());
        Assert.True(connectionString.ForeignKeys);
        Assert.False(connectionString.Pooling);
        Assert.Equal(SqliteCacheMode.Private, connectionString.Cache);
        Assert.Equal(30, connectionString.DefaultTimeout);
        await using var connection = new SqliteConnection(connectionString.ToString());
        await connection.OpenAsync(Cancellation);
        Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys"));
        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode"));
        Assert.Equal(2L, await ScalarAsync(connection, "PRAGMA synchronous"));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT count(*) FROM pragma_foreign_key_list('SuggestedProfileUpdates') WHERE on_delete = 'CASCADE'"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM pragma_index_list('PersonExternalConnections') WHERE name = 'IX_PersonConnection_StableIdentity' AND \"unique\" = 1"));

        database.People.Add(new Person { Id = Guid.NewGuid(), UserProfileId = Guid.NewGuid(), DisplayName = "Orphan" });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));
        Assert.Equal(787, Assert.IsType<SqliteException>(failure.InnerException).SqliteExtendedErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stable_identity_database_collision_maps_to_conflict_without_provider_details(bool person)
    {
        var profileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(new() { UserProfileId = profileId, DisplayName = "Person" });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var scope = fixture.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (person)
                database.PersonExternalConnections.Add(new() { Id = Guid.NewGuid(), PersonId = personId, Platform = "example", ExternalUserId = "private-id" });
            else
                database.UserProfileExternalConnections.Add(new() { Id = Guid.NewGuid(), UserProfileId = profileId, Platform = "example", ExternalUserId = "private-id" });
            if (attempt == 0) await ExternalConnectionPersistence.SaveAsync(database, Cancellation);
            else
            {
                var failure = await Assert.ThrowsAsync<ConflictException>(() => ExternalConnectionPersistence.SaveAsync(database, Cancellation));
                Assert.DoesNotContain("private-id", failure.ToString());
                Assert.Null(failure.InnerException);
            }
        }
    }

    [Fact]
    public async Task Other_constraint_failures_are_not_misreported_as_identity_conflicts()
    {
        var profileId = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.UserProfileExternalConnections.Add(new() { Id = Guid.NewGuid(), UserProfileId = profileId, Platform = new string('a', 33) });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => ExternalConnectionPersistence.SaveAsync(database, Cancellation));
        Assert.Equal(275, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task Snapshot_is_consistent_without_reserving_the_writer_and_write_transactions_reject_stale_upgrades()
    {
        var profileId = await fixture.CreateUserProfileAsync("Before");
        await using var firstScope = fixture.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var snapshot = await first.Database.BeginTransactionAsync(IsolationLevel.Serializable, Cancellation);
        Assert.Equal("Before", await first.UserProfiles.Where(profile => profile.Id == profileId).Select(profile => profile.DisplayName).SingleAsync(Cancellation));
        await using (var secondScope = fixture.CreateAsyncScope())
        {
            var second = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await second.UserProfiles.SingleAsync(profile => profile.Id == profileId, Cancellation);
            profile.DisplayName = "After";
            await second.SaveChangesAsync(Cancellation);
        }
        Assert.Equal("Before", await first.UserProfiles.Where(profile => profile.Id == profileId).Select(profile => profile.DisplayName).SingleAsync(Cancellation));
        first.Database.SetCommandTimeout(1);
        var failure = await Assert.ThrowsAsync<SqliteException>(() => first.LockAnalysisContextAsync(profileId, Cancellation));
        Assert.Equal(517, failure.SqliteExtendedErrorCode); // SQLITE_BUSY_SNAPSHOT
    }

    [Fact]
    public async Task First_guard_creation_is_unique_and_a_second_connection_cannot_write_until_commit()
    {
        var profileId = Guid.NewGuid();
        await using var firstScope = fixture.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await first.Database.BeginTransactionAsync(Cancellation);
        await first.LockAnalysisContextAsync(profileId, Cancellation);
        var connectionString = new SqliteConnectionStringBuilder(first.Database.GetConnectionString()) { DefaultTimeout = 1 };
        await using var second = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString.ToString()).Options);
        // A real competing writer times out while the first transaction is held, not a timing guess.
        var failure = await Assert.ThrowsAsync<SqliteException>(() => second.Database.BeginTransactionAsync(Cancellation));
        Assert.Equal(5, failure.SqliteErrorCode);
        await transaction.CommitAsync(Cancellation);
        await using var retry = await second.Database.BeginTransactionAsync(Cancellation);
        await second.LockAnalysisContextAsync(profileId, Cancellation);
        await retry.CommitAsync(Cancellation);
        Assert.Equal(1, await second.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM AnalysisContextGuards WHERE UserProfileId = {profileId}").SingleAsync(Cancellation));
    }

    [Fact]
    public async Task Sqlite_save_telemetry_excludes_sql_private_values_database_paths_and_exception_details()
    {
        using var root = new Activity("sqlite-privacy-check").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SocialTelemetryTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (activity.TraceId == root.TraceId) spans.Enqueue(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.People.Add(new Person { Id = Guid.NewGuid(), UserProfileId = Guid.NewGuid(), DisplayName = "PRIVATE-SQLITE-MARKER" });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));
        var span = Assert.Single(spans, activity => activity.DisplayName == "database.save");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("sqlite", span.GetTagItem("db.system.name"));
        var exported = $"{span.StatusDescription} {string.Join(' ', span.TagObjects)} {string.Join(' ', span.Events.Select(entry => string.Join(' ', entry.Tags)))}";
        Assert.DoesNotContain("PRIVATE-SQLITE-MARKER", exported);
        Assert.DoesNotContain(fixture.AttachmentStorageDirectory, exported);
        Assert.DoesNotContain("INSERT", exported);
        Assert.DoesNotContain("FOREIGN KEY", exported);
        Assert.Empty(span.Events);
    }

    private static async Task<object> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Cancellation) ?? throw new InvalidOperationException();
    }
}
