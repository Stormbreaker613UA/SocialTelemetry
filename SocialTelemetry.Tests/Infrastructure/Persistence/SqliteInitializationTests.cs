using System.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Tests.Infrastructure.Persistence;

[Collection("SQLite")]
public sealed class SqliteInitializationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("delete-with-data")]
    public async Task Startup_establishes_wal_and_preserves_data_with_overlapping_reader_and_writer(string initialState)
    {
        var root = TemporaryRoot();
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "socialtelemetry.db");
        var profileId = Guid.NewGuid();
        try
        {
            if (initialState == "empty") await File.WriteAllBytesAsync(file, [], Cancellation);
            if (initialState == "delete-with-data")
            {
                await using (var database = Context(file))
                {
                    await database.Database.MigrateAsync(Cancellation);
                    database.UserProfiles.Add(new UserProfile { Id = profileId, DisplayName = "Before" });
                    await database.SaveChangesAsync(Cancellation);
                }
                await using var connection = Connection(file);
                await connection.OpenAsync(Cancellation);
                Assert.Equal("delete", await ScalarAsync(connection, "PRAGMA journal_mode=DELETE"));
            }
            using var application = Application(root);
            using var client = application.CreateClient();
            await using (var database = Context(file))
            {
                if (initialState != "delete-with-data")
                {
                    database.UserProfiles.Add(new UserProfile { Id = profileId, DisplayName = "Before" });
                    await database.SaveChangesAsync(Cancellation);
                }
                Assert.Equal("Before", (await database.UserProfiles.SingleAsync(profile => profile.Id == profileId, Cancellation)).DisplayName);
            }
            await using var reader = Connection(file);
            await reader.OpenAsync(Cancellation);
            Assert.Equal("wal", await ScalarAsync(reader, "PRAGMA journal_mode"));
            Assert.Equal(2L, await ScalarAsync(reader, "PRAGMA synchronous"));
            await using var snapshot = reader.BeginTransaction(IsolationLevel.Serializable, deferred: true);
            Assert.Equal("Before", await ScalarAsync(reader, "SELECT DisplayName FROM UserProfiles", snapshot));
            await using (var writer = Context(file))
            {
                var profile = await writer.UserProfiles.SingleAsync(profile => profile.Id == profileId, Cancellation);
                profile.DisplayName = "After";
                await writer.SaveChangesAsync(Cancellation);
            }
            Assert.Equal("Before", await ScalarAsync(reader, "SELECT DisplayName FROM UserProfiles", snapshot));
            await snapshot.CommitAsync(Cancellation);
            Assert.Equal("After", await ScalarAsync(reader, "SELECT DisplayName FROM UserProfiles"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unavailable_wal_stops_startup_safely_before_cleanup()
    {
        var root = TemporaryRoot();
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "socialtelemetry.db");
        try
        {
            await using (var database = Context(file)) await database.Database.MigrateAsync(Cancellation);
            await using var reader = Connection(file);
            await reader.OpenAsync(Cancellation);
            Assert.Equal("delete", await ScalarAsync(reader, "PRAGMA journal_mode=DELETE"));
            await using var transaction = reader.BeginTransaction(IsolationLevel.Serializable, deferred: true);
            await ScalarAsync(reader, "SELECT count(*) FROM UserProfiles", transaction);
            var storage = Path.Combine(root, "attachments");
            Directory.CreateDirectory(storage);
            var orphan = Path.Combine(storage, Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(orphan, "synthetic", Cancellation);
            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-3));
            using var application = Application(root);
            var failure = Assert.ThrowsAny<Exception>(() => application.CreateClient());
            Assert.Contains("SQLite initialization failed", failure.Message);
            Assert.DoesNotContain(root, failure.ToString());
            Assert.DoesNotContain("PRAGMA", failure.ToString());
            Assert.DoesNotContain("database is locked", failure.ToString());
            Assert.Null(failure.InnerException);
            Assert.True(File.Exists(orphan));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static WebApplicationFactory<Program> Application(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite", ["Persistence:SqliteTimeoutSeconds"] = "1",
                ["ApplicationData:RootDirectory"] = root, ["ConnectionStrings:Default"] = ""
            })));

    private static SqliteConnection Connection(string file) => new(new SqliteConnectionStringBuilder
    { DataSource = file, ForeignKeys = true, Pooling = false, DefaultTimeout = 1 }.ToString());

    private static AppDbContext Context(string file) => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Connection(file).ConnectionString).Options);

    private static async Task<object> ScalarAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return await command.ExecuteScalarAsync(Cancellation) ?? throw new InvalidOperationException();
    }

    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
}
