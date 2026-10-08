using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Runtime;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Tests.Infrastructure.Configuration;

[Collection("SQLite")]
public sealed class PersistenceConfigurationTests
{
    [Theory]
    [InlineData("Persistence:Provider", "Unknown", "Persistence:Provider")]
    [InlineData("ConnectionStrings:Default", "", "ConnectionStrings:Default")]
    public void Invalid_configuration_fails_clearly_without_fallback(string key, string value, string expected)
    {
        using var application = CreateApp(new() { [key] = value }, maintenance: false);
        var failure = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(expected, failure.ToString());
    }

    [Fact]
    public void PostgreSql_does_not_create_a_sqlite_file_or_validate_unused_sqlite_path()
    {
        var root = TemporaryRoot();
        try
        {
            using var application = CreateApp(new()
            {
                ["ApplicationData:RootDirectory"] = root,
                ["Persistence:Provider"] = "PostgreSql",
                ["Persistence:SqliteFile"] = ""
            }, maintenance: false);
            using var client = application.CreateClient();
            using var scope = application.Services.CreateScope();
            Assert.True(scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.IsNpgsql());
            Assert.False(Directory.Exists(root));
        }
        finally { DeleteTemporaryRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sqlite_starts_without_PostgreSql_and_resolves_relative_or_absolute_file_through_application_paths(bool absolute)
    {
        var root = TemporaryRoot();
        var expected = Path.Combine(root, "database", "custom.db");
        try
        {
            using var application = CreateApp(new()
            {
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:SqliteFile"] = absolute ? expected : Path.Combine("database", "custom.db"),
                ["ConnectionStrings:Default"] = "",
                ["ApplicationData:RootDirectory"] = root
            });
            using var client = application.CreateClient();
            using var scope = application.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(database.Database.IsSqlite());
            Assert.True(File.Exists(expected));
            var paths = application.Services.GetRequiredService<ApplicationPaths>();
            Assert.Equal(expected, paths.DatabaseFile(application.Services.GetRequiredService<IOptions<PersistenceOptions>>().Value.SqliteFile));
            Assert.Equal(Path.Combine(root, "attachments"), paths.Attachments(new()));
            Assert.False(application.Services.GetRequiredService<IOptions<SocialTelemetry.Api.Infrastructure.Observability.ObservabilityOptions>>().Value.OtlpEnabled);
        }
        finally { DeleteTemporaryRoot(root); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(":memory:")]
    public void Sqlite_rejects_invalid_or_non_file_paths(string path)
    {
        var root = TemporaryRoot();
        try
        {
            using var application = CreateApp(new()
            {
                ["Persistence:Provider"] = "Sqlite", ["Persistence:SqliteFile"] = path,
                ["ApplicationData:RootDirectory"] = root, ["ConnectionStrings:Default"] = ""
            });
            var failure = Assert.ThrowsAny<Exception>(() => application.CreateClient());
            Assert.Contains("Persistence:SqliteFile", failure.ToString());
        }
        finally { DeleteTemporaryRoot(root); }
    }

    [Fact]
    public void Design_time_requires_an_explicit_provider()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new AppDbContextFactory().CreateDbContext([]));
        Assert.Contains("--provider", failure.Message);
    }

    [Theory]
    [InlineData("PostgreSql", 0, true)]
    [InlineData("PostgreSql", 121, true)]
    [InlineData("Sqlite", 1, true)]
    [InlineData("Sqlite", 120, true)]
    [InlineData("Sqlite", 0, false)]
    [InlineData("Sqlite", 121, false)]
    public void Timeout_validation_matches_runtime_and_design_time(string provider, int timeout, bool valid)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "SocialTelemetry.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        Directory.SetCurrentDirectory(repository.FullName);
        var root = TemporaryRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = provider,
            ["Persistence:SqliteTimeoutSeconds"] = timeout.ToString(),
            ["ApplicationData:RootDirectory"] = root,
            ["ConnectionStrings:Default"] = "Host=localhost;Database=unused;Username=unused"
        };
        try
        {
            using var application = CreateApp(settings, maintenance: false);
            if (valid)
            {
                using var client = application.CreateClient();
                using var context = new AppDbContextFactory().CreateDbContext(
                    ["--provider", provider, "--Persistence:SqliteTimeoutSeconds", timeout.ToString(),
                     "--ApplicationData:RootDirectory", root, "--ConnectionStrings:Default", settings["ConnectionStrings:Default"]!]);
                Assert.Equal(provider == "Sqlite", context.Database.IsSqlite());
            }
            else
            {
                Assert.Contains("SqliteTimeoutSeconds", Assert.ThrowsAny<Exception>(() => application.CreateClient()).ToString());
                Assert.Contains("SqliteTimeoutSeconds", Assert.Throws<InvalidOperationException>(() => new AppDbContextFactory().CreateDbContext(
                    ["--provider", provider, "--Persistence:SqliteTimeoutSeconds", timeout.ToString(),
                     "--ApplicationData:RootDirectory", root])).Message);
            }
        }
        finally { Directory.SetCurrentDirectory(originalDirectory); DeleteTemporaryRoot(root); }
    }

    private static WebApplicationFactory<Program> CreateApp(Dictionary<string, string?> values, bool maintenance = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values));
            if (!maintenance)
                builder.ConfigureServices(services =>
                {
                    foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                        (service.ImplementationType == typeof(AttachmentReconciliationService) ||
                         service.ImplementationType == typeof(ProfileAvatarReconciliationService) ||
                         service.ImplementationType == typeof(MediaDirectoryBinding))).ToArray())
                        services.Remove(service);
                });
        });

    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
    private static void DeleteTemporaryRoot(string root) { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
