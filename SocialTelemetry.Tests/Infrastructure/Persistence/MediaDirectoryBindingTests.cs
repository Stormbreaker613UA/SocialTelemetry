using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using Testcontainers.PostgreSql;

namespace SocialTelemetry.Tests.Infrastructure.Persistence;

[Collection("SQLite")]
public sealed class MediaDirectoryBindingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("binding_tests").WithUsername("postgres").WithPassword("postgres").Build();
    private readonly List<string> roots = [];
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await postgres.StartAsync(Cancellation);
    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        foreach (var root in roots) if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("PostgreSql", "Sqlite", false)]
    [InlineData("PostgreSql", "Sqlite", true)]
    [InlineData("Sqlite", "PostgreSql", true)]
    [InlineData("Sqlite", "Sqlite", false)]
    [InlineData("Sqlite", "Sqlite", true)]
    [InlineData("PostgreSql", "PostgreSql", true)]
    public async Task Another_database_cannot_reconcile_original_media_and_correct_restart_cleans_only_orphans(
        string originalProvider, string otherProvider, bool absolute)
    {
        var root = Root();
        var original = await SettingsAsync(originalProvider, root, absolute);
        var other = await SettingsAsync(otherProvider, root, absolute);
        Media media;
        using (var application = App(original))
        using (var client = application.CreateClient()) media = await SeedMediaAsync(application, original);
        using (var conflicting = App(other))
            Assert.Throws<MediaBindingException>(() => conflicting.CreateClient());
        AssertMediaSurvives(media);
        await using (var originalDatabase = Context(original))
        {
            Assert.True(await originalDatabase.InteractionAttachments.AnyAsync(attachment => attachment.StorageKey == media.AttachmentKey, Cancellation));
            Assert.True(await originalDatabase.UserProfiles.AnyAsync(profile => profile.AvatarStorageKey == media.AvatarKey, Cancellation));
        }
        using var restarted = App(original);
        using var restartedClient = restarted.CreateClient();
        Assert.True(File.Exists(media.Attachment));
        Assert.True(File.Exists(media.Avatar));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(media.Attachment)!, media.PendingKey)));
        Assert.False(File.Exists(media.Orphan));
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("PostgreSql")]
    public async Task Recreated_database_at_same_locator_has_new_identity_and_cannot_claim_original_media(string provider)
    {
        var settings = await SettingsAsync(provider, Root(), absolute: true);
        Media media;
        Guid originalId;
        using (var application = App(settings))
        using (var client = application.CreateClient())
        {
            media = await SeedMediaAsync(application, settings);
            originalId = (await ContextIdentityAsync(settings)).StoreId;
        }
        if (provider == "Sqlite") File.Delete(settings["Persistence:SqliteFile"]!);
        else
        {
            var connection = new NpgsqlConnectionStringBuilder(settings["ConnectionStrings:Default"]);
            await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
            await admin.OpenAsync(Cancellation);
            NpgsqlConnection.ClearAllPools();
            await using var command = admin.CreateCommand();
            command.CommandText = $"DROP DATABASE \"{connection.Database}\" WITH (FORCE); CREATE DATABASE \"{connection.Database}\"";
            await command.ExecuteNonQueryAsync(Cancellation);
        }
        await using (var database = Context(settings)) await database.Database.MigrateAsync(Cancellation);
        using var conflicting = App(settings);
        Assert.Throws<MediaBindingException>(() => conflicting.CreateClient());
        Assert.NotEqual(originalId, (await ContextIdentityAsync(settings)).StoreId);
        AssertMediaSurvives(media);
    }

    [Fact]
    public async Task Copied_database_identity_at_another_locator_is_rejected()
    {
        var settings = await SettingsAsync("Sqlite", Root(), absolute: true);
        Media media;
        using (var application = App(settings))
        using (var client = application.CreateClient()) media = await SeedMediaAsync(application, settings);
        var copied = new Dictionary<string, string?>(settings);
        copied["Persistence:SqliteFile"] = Path.Combine(settings["ApplicationData:RootDirectory"]!, "copied.db");
        File.Copy(settings["Persistence:SqliteFile"]!, copied["Persistence:SqliteFile"]!);
        Assert.Equal((await ContextIdentityAsync(settings)).StoreId, (await ContextIdentityAsync(copied)).StoreId);
        using var conflicting = App(copied);
        Assert.Throws<MediaBindingException>(() => conflicting.CreateClient());
        AssertMediaSurvives(media);
        var oldMarker = Marker(settings);
        var identity = await ContextIdentityAsync(copied);
        // Rebind only after all hosts have stopped, with both old and new inspected identities.
        using (var maintenance = MaintenanceApp(copied, "inspect", identity.StoreId, null))
            await Binding(maintenance).RunMaintenanceAsync(Cancellation);
        var independentDirectories = new Dictionary<string, string?>(copied)
        {
            ["AttachmentStorage:LocalDirectory"] = Path.Combine(Root(), "attachments"),
            ["ProfileStorage:AvatarDirectory"] = Path.Combine(Root(), "avatars")
        };
        string newLocator;
        using (var reference = App(independentDirectories))
        using (var client = reference.CreateClient()) newLocator = Marker(independentDirectories).GetProperty("Locator").GetString()!;
        using (var maintenance = MaintenanceApp(copied, "rebind", identity.StoreId, newLocator))
        {
            var options = maintenance.Services.GetRequiredService<IOptions<MediaBindingOptions>>().Value;
            options.PreviousStoreId = oldMarker.GetProperty("StoreId").GetGuid();
            options.PreviousLocator = oldMarker.GetProperty("Locator").GetString();
            await Binding(maintenance).RunMaintenanceAsync(Cancellation);
        }
        AssertMediaSurvives(media);
        using var rebound = App(copied);
        using var reboundClient = rebound.CreateClient();
        Assert.True(File.Exists(media.Attachment));
        Assert.True(File.Exists(media.Avatar));
    }

    [Fact]
    public async Task Legacy_media_requires_explicit_offline_adoption_without_running_cleanup()
    {
        var settings = await SettingsAsync("PostgreSql", Root(), absolute: true);
        Media media;
        using (var preparation = App(settings))
        using (var client = preparation.CreateClient()) media = await SeedMediaAsync(preparation, settings);
        var markerLocator = Marker(settings).GetProperty("Locator").GetString()!;
        foreach (var directory in Directories(settings)) File.Delete(Path.Combine(directory, ".socialtelemetry-media-owner.json"));
        using (var rejected = App(settings)) Assert.Throws<MediaBindingException>(() => rejected.CreateClient());
        AssertMediaSurvives(media);
        var identity = await ContextIdentityAsync(settings);
        // The safe locator comes from inspection; no connection string or path is printed.
        using (var maintenance = MaintenanceApp(settings, "inspect", identity.StoreId, null))
            await Binding(maintenance).RunMaintenanceAsync(Cancellation);
        using (var maintenance = MaintenanceApp(settings, "adopt", identity.StoreId, markerLocator))
            await Binding(maintenance).RunMaintenanceAsync(Cancellation);
        AssertMediaSurvives(media);
        using var restarted = App(settings);
        using var restartedClient = restarted.CreateClient();
        Assert.True(File.Exists(media.Attachment));
        Assert.True(File.Exists(media.Avatar));
        Assert.False(File.Exists(media.Orphan));
    }

    [Fact]
    public async Task Active_directory_lease_prevents_rebinding_and_same_owner_hosts_can_overlap()
    {
        var settings = await SettingsAsync("Sqlite", Root(), absolute: true);
        using var first = App(settings);
        using var firstClient = first.CreateClient();
        using var second = App(settings);
        using var secondClient = second.CreateClient();
        var marker = Marker(settings);
        using var maintenance = MaintenanceApp(settings, "rebind", marker.GetProperty("StoreId").GetGuid(), marker.GetProperty("Locator").GetString());
        var failure = await Assert.ThrowsAsync<MediaBindingException>(() => Binding(maintenance).RunMaintenanceAsync(Cancellation));
        Assert.Contains("in use", failure.Message);
    }

    [Fact]
    public async Task Staging_marker_conflict_stops_startup_before_any_orphan_cleanup()
    {
        var settings = await SettingsAsync("Sqlite", Root(), absolute: true);
        Media media;
        using (var application = App(settings))
        using (var client = application.CreateClient()) media = await SeedMediaAsync(application, settings);
        var file = Path.Combine(settings["AttachmentStorage:LocalDirectory"]!, ".staging", ".socialtelemetry-media-owner.json");
        var marker = File.ReadAllText(file).Replace((await ContextIdentityAsync(settings)).StoreId.ToString(), Guid.NewGuid().ToString());
        File.WriteAllText(file, marker);
        using var conflicting = App(settings);
        Assert.Throws<MediaBindingException>(() => conflicting.CreateClient());
        AssertMediaSurvives(media);
    }

    [Fact]
    public async Task Nested_absolute_override_cannot_claim_another_stores_directory()
    {
        var root = Root();
        var original = await SettingsAsync("Sqlite", root, absolute: true);
        Media media;
        using (var application = App(original))
        using (var client = application.CreateClient()) media = await SeedMediaAsync(application, original);
        var other = await SettingsAsync("Sqlite", root, absolute: true);
        other["AttachmentStorage:LocalDirectory"] = Path.Combine(original["AttachmentStorage:LocalDirectory"]!, "nested");
        other["ProfileStorage:AvatarDirectory"] = Path.Combine(other["AttachmentStorage:LocalDirectory"]!, "avatars");
        using var conflicting = App(other);
        Assert.Throws<MediaBindingException>(() => conflicting.CreateClient());
        AssertMediaSurvives(media);
    }

    [Fact]
    public async Task Incorrect_explicit_rebinding_identity_leaves_all_markers_and_media_unchanged()
    {
        var root = Root();
        var original = await SettingsAsync("Sqlite", root, absolute: true);
        Media media;
        using (var application = App(original))
        using (var client = application.CreateClient()) media = await SeedMediaAsync(application, original);
        var before = Directories(original).Select(directory => File.ReadAllText(Path.Combine(directory, ".socialtelemetry-media-owner.json"))).ToArray();
        var other = await SettingsAsync("Sqlite", root, absolute: true);
        // Inspection initializes the new identity but never assigns media ownership.
        using (var inspection = MaintenanceApp(other, "inspect", Guid.Empty, null))
            await Binding(inspection).RunMaintenanceAsync(Cancellation);
        var identity = await ContextIdentityAsync(other);
        using var maintenance = MaintenanceApp(other, "rebind", identity.StoreId, new string('0', 64));
        var options = maintenance.Services.GetRequiredService<IOptions<MediaBindingOptions>>().Value;
        options.PreviousStoreId = Guid.NewGuid();
        options.PreviousLocator = new string('0', 64);
        await Assert.ThrowsAsync<MediaBindingException>(() => Binding(maintenance).RunMaintenanceAsync(Cancellation));
        Assert.Equal(before, Directories(original).Select(directory => File.ReadAllText(Path.Combine(directory, ".socialtelemetry-media-owner.json"))).ToArray());
        AssertMediaSurvives(media);
    }

    [Theory]
    [InlineData("Sqlite", true)]
    [InlineData("Sqlite", false)]
    [InlineData("PostgreSql", true)]
    [InlineData("PostgreSql", false)]
    public async Task Concurrent_first_binding_agrees_on_identity_and_excludes_conflicting_stores(string provider, bool sameStore)
    {
        var root = Root();
        var firstSettings = await SettingsAsync(provider, root, absolute: true);
        var secondSettings = sameStore ? firstSettings : await SettingsAsync(provider, root, absolute: true);
        // First-time durable marker writes must fit the normal startup budget, not the short refusal-test timeout.
        firstSettings["MediaBinding:LockTimeout"] = "00:00:05";
        secondSettings["MediaBinding:LockTimeout"] = "00:00:05";
        using var first = MaintenanceApp(firstSettings, "inspect", Guid.Empty, null);
        using var second = MaintenanceApp(secondSettings, "inspect", Guid.Empty, null);
        first.Services.GetRequiredService<IOptions<MediaBindingOptions>>().Value.Action = null;
        second.Services.GetRequiredService<IOptions<MediaBindingOptions>>().Value.Action = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> StartAsync(MediaDirectoryBinding service)
        {
            await ready.Task.WaitAsync(Cancellation);
            try { await service.StartAsync(Cancellation); return true; }
            catch (MediaBindingException) { return false; }
        }
        var tasks = new[] { Task.Run(() => StartAsync(Binding(first)), Cancellation), Task.Run(() => StartAsync(Binding(second)), Cancellation) };
        ready.SetResult();
        var outcomes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        Assert.Equal(sameStore ? 2 : 1, outcomes.Count(success => success));
        if (sameStore)
            Assert.Equal((await ContextIdentityAsync(firstSettings)).StoreId, (await ContextIdentityAsync(secondSettings)).StoreId);
    }

    private async Task<Dictionary<string, string?>> SettingsAsync(string provider, string root, bool absolute)
    {
        var databaseName = "binding_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = databaseName, Pooling = false };
        if (provider == "PostgreSql")
        {
            await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
            await admin.OpenAsync(Cancellation);
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync(Cancellation);
        }
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = provider, ["ApplicationData:RootDirectory"] = root,
            ["Persistence:SqliteFile"] = Path.Combine(root, databaseName + ".db"),
            ["ConnectionStrings:Default"] = connection.ToString(), ["MediaBinding:LockTimeout"] = "00:00:00.1"
        };
        if (absolute)
        {
            settings["AttachmentStorage:LocalDirectory"] = Path.Combine(root, "absolute-media");
            settings["ProfileStorage:AvatarDirectory"] = Path.Combine(root, "absolute-avatars");
        }
        else
        {
            settings["AttachmentStorage:LocalDirectory"] = "attachments";
            settings["ProfileStorage:AvatarDirectory"] = "attachments/avatars";
        }
        await using var database = Context(settings);
        await database.Database.MigrateAsync(Cancellation);
        return settings;
    }

    private static WebApplicationFactory<Program> App(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));

    private static WebApplicationFactory<Program> MaintenanceApp(Dictionary<string, string?> settings, string action, Guid storeId, string? locator)
    {
        var application = App(settings).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // Exercise the service without running cleanup or HTTP maintenance commands.
            foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                (service.ImplementationType == typeof(MediaDirectoryBinding) || service.ImplementationType == typeof(AttachmentReconciliationService) ||
                 service.ImplementationType == typeof(ProfileAvatarReconciliationService))).ToArray()) services.Remove(service);
            services.AddSingleton<MediaDirectoryBinding>();
        }));
        var options = application.Services.GetRequiredService<IOptions<MediaBindingOptions>>().Value;
        options.Action = action;
        options.StoreId = storeId;
        options.Locator = locator;
        return application;
    }

    private static MediaDirectoryBinding Binding(WebApplicationFactory<Program> application) => application.Services.GetRequiredService<MediaDirectoryBinding>();

    private static AppDbContext Context(Dictionary<string, string?> settings)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (settings["Persistence:Provider"] == "Sqlite")
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = settings["Persistence:SqliteFile"], ForeignKeys = true, Pooling = false }.ToString());
        else options.UseNpgsql(settings["ConnectionStrings:Default"]);
        return new AppDbContext(options.Options);
    }

    private static async Task<StorageDatabaseIdentity> ContextIdentityAsync(Dictionary<string, string?> settings)
    {
        await using var database = Context(settings);
        return await database.StorageDatabaseIdentities.AsNoTracking().SingleAsync(Cancellation);
    }

    private static string[] Directories(Dictionary<string, string?> settings)
    {
        var root = settings["ApplicationData:RootDirectory"]!;
        var attachment = Path.GetFullPath(settings["AttachmentStorage:LocalDirectory"]!, root);
        var avatar = Path.GetFullPath(settings["ProfileStorage:AvatarDirectory"]!, root);
        return [attachment, Path.Combine(attachment, ".staging"), avatar, Path.Combine(avatar, ".staging")];
    }

    private static JsonElement Marker(Dictionary<string, string?> settings) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Directories(settings)[0], ".socialtelemetry-media-owner.json"))).RootElement.Clone();

    private static async Task<Media> SeedMediaAsync(WebApplicationFactory<Program> application, Dictionary<string, string?> settings)
    {
        var directories = Directories(settings);
        var attachmentKey = Guid.NewGuid().ToString("N");
        var avatarKey = Guid.NewGuid().ToString("N");
        var pendingKey = Guid.NewGuid().ToString("N");
        var profile = new UserProfile { Id = Guid.NewGuid(), DisplayName = "Synthetic", AvatarStorageKey = avatarKey, AvatarMimeType = "image/png" };
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = profile.Id, Title = "Synthetic" };
        await using var scope = application.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.UserProfiles.Add(profile);
        database.Interactions.Add(interaction);
        database.InteractionAttachments.AddRange(
            new InteractionAttachment { Id = Guid.NewGuid(), InteractionId = interaction.Id, Type = AttachmentType.Image, Status = AttachmentStatus.Ready, StorageKey = attachmentKey, MimeType = "image/png" },
            new InteractionAttachment { Id = Guid.NewGuid(), InteractionId = interaction.Id, Type = AttachmentType.Image, Status = AttachmentStatus.Pending, StorageKey = pendingKey, MimeType = "image/png" });
        await database.SaveChangesAsync(Cancellation);
        var files = new[] { Path.Combine(directories[0], attachmentKey), Path.Combine(directories[2], avatarKey),
            Path.Combine(directories[1], pendingKey), Path.Combine(directories[0], Guid.NewGuid().ToString("N")) };
        foreach (var file in files) { await File.WriteAllTextAsync(file, "synthetic", Cancellation); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3)); }
        return new Media(files[0], files[1], files[2], files[3], attachmentKey, avatarKey, pendingKey);
    }

    private static void AssertMediaSurvives(Media media)
    {
        foreach (var file in new[] { media.Attachment, media.Avatar, media.Staged, media.Orphan }) Assert.True(File.Exists(file));
    }

    private string Root()
    {
        var root = Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); roots.Add(root); return root;
    }
    private sealed record Media(string Attachment, string Avatar, string Staged, string Orphan, string AttachmentKey, string AvatarKey, string PendingKey);
}
