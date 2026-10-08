using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Runtime;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class MediaDirectoryBinding(IServiceScopeFactory scopeFactory, ApplicationPaths paths,
    IOptions<AttachmentStorageOptions> attachments, IOptions<ProfileStorageOptions> profiles,
    IOptions<MediaBindingOptions> options) : IHostedService, IDisposable
{
    private const string MarkerName = ".socialtelemetry-media-owner.json";
    private const string UseLockName = ".socialtelemetry-media-use.lock";
    private const string BindingLockName = ".socialtelemetry-media-binding.lock";
    private readonly List<FileStream> leases = [];
    private readonly object leaseGate = new();
    private bool disposed;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await BindAsync(maintenance: false, cancellationToken); }
        catch { Dispose(); throw; }
    }

    public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
    public void Dispose()
    {
        FileStream[] ownedLeases;
        lock (leaseGate)
        {
            disposed = true;
            ownedLeases = leases.ToArray();
            leases.Clear();
        }
        foreach (var lease in ownedLeases) lease.Dispose();
    }

    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        try { await BindAsync(maintenance: true, cancellationToken); }
        finally { Dispose(); }
    }

    private async Task BindAsync(bool maintenance, CancellationToken cancellationToken)
    {
        var bindingLocks = new List<FileStream>();
        try
        {
            var areas = Areas();
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storeId = await GetStoreIdAsync(database, cancellationToken);
            var locator = DatabaseLocator(database);
            if (maintenance && options.Value.Action == "inspect")
            {
                Console.WriteLine($"StoreId: {storeId:D}\nLocator: {locator}");
                foreach (var area in areas)
                {
                    CheckPath(area.Directory);
                    var marker = ReadMarker(area.Directory);
                    Console.WriteLine($"{area.Kind}: {(marker is null ? "unbound" : $"StoreId={marker.StoreId:D} Locator={marker.Locator}")} Directory={Hash(area.Directory)}");
                }
                return;
            }
            if (maintenance && (options.Value.Action is not ("adopt" or "rebind") ||
                options.Value.StoreId != storeId || options.Value.Locator != locator))
                throw Failure("Maintenance requires the exact inspected StoreId and Locator.");

            foreach (var area in areas)
            {
                CheckPath(area.Directory);
                CheckAncestorOwners(area.Directory, storeId, locator);
                Directory.CreateDirectory(area.Directory);
                // Shared lifetime leases permit the same owner in several hosts, but exclude offline rebinding.
                var lease = await LockAsync(Path.Combine(area.Directory, UseLockName), exclusive: maintenance, cancellationToken);
                lock (leaseGate)
                {
                    if (disposed) { lease.Dispose(); throw Failure("Media binding is shutting down."); }
                    leases.Add(lease);
                }
                bindingLocks.Add(await LockAsync(Path.Combine(area.Directory, BindingLockName), exclusive: true, cancellationToken));
            }
            // Validate every area before writing any ownership marker or allowing either reconciliation service.
            foreach (var area in areas)
            {
                var marker = ReadMarker(area.Directory);
                var expected = new Binding(storeId, locator, area.Kind, Hash(area.Directory));
                if (marker == expected) continue;
                if (!maintenance)
                {
                    if (marker is not null || !IsEmpty(area.Directory, areas))
                        throw Failure("Media ownership is missing or conflicts with the selected database. Use offline inspection/adoption with verified data.");
                }
                else if (options.Value.Action == "adopt")
                {
                    if (marker is not null)
                        throw Failure("Adoption cannot replace an existing media owner. Use an explicit verified rebind.");
                }
                else if (marker is null || marker.StoreId != options.Value.PreviousStoreId ||
                    marker.Locator != options.Value.PreviousLocator || marker.Kind != area.Kind)
                    throw Failure("Rebinding requires the exact previous owner and locator for every conflicting area.");
            }
            foreach (var area in areas)
            {
                var binding = new Binding(storeId, locator, area.Kind, Hash(area.Directory));
                if (ReadMarker(area.Directory) != binding) WriteMarker(area.Directory, binding);
            }
            if (maintenance) Console.WriteLine("Media binding completed. No media files were moved or deleted; reconciliation did not run.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MediaBindingException) { throw; }
        catch (Exception) { throw Failure("Media binding failed. Check migrations, directory access and ownership before starting the application."); }
        finally { foreach (var file in bindingLocks) file.Dispose(); }
    }

    private static async Task<Guid> GetStoreIdAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        var candidate = Guid.NewGuid();
        // Both providers support this insert; concurrent first starts agree on the persisted winner.
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StorageDatabaseIdentity" ("Id", "StoreId") VALUES (1, {candidate})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);
        var identity = await database.StorageDatabaseIdentities.AsNoTracking().SingleAsync(cancellationToken);
        if (identity.Id != 1 || identity.StoreId == Guid.Empty) throw Failure("Database StoreId is invalid.");
        return identity.StoreId;
    }

    private static string DatabaseLocator(AppDbContext database)
    {
        if (database.Database.IsSqlite())
        {
            var file = Canonical(database.Database.GetDbConnection().DataSource);
            return Hash("Sqlite|" + file);
        }
        if (database.Database.IsNpgsql())
        {
            var connection = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString());
            var databaseName = connection.Database ?? database.Database.GetDbConnection().Database;
            return Hash($"PostgreSql|{(connection.Host ?? "").ToLowerInvariant()}|{connection.Port}|{databaseName}|{connection.SearchPath}");
        }
        throw Failure("Unsupported media database provider.");
    }

    private Area[] Areas()
    {
        var attachmentRoot = Canonical(paths.Attachments(attachments.Value));
        var avatarRoot = Canonical(paths.Avatars(profiles.Value, attachments.Value));
        Area[] areas = [new(attachmentRoot, "attachments"), new(Canonical(Path.Combine(attachmentRoot, ".staging")), "attachment-staging"),
            new(avatarRoot, "avatars"), new(Canonical(Path.Combine(avatarRoot, ".staging")), "avatar-staging")];
        if (areas.Select(area => area.Directory).Distinct(PathComparer).Count() != areas.Length)
            throw Failure("Media areas overlap.");
        // Check the calculated layout before any directories/markers exist; ancestor markers alone
        // would otherwise allow the first startup and reject the same layout after restart.
        foreach (var staging in areas.Where(area => area.Kind.EndsWith("-staging", StringComparison.Ordinal)))
        {
            foreach (var media in areas.Where(area => area.Kind is "attachments" or "avatars"))
            {
                if (IsWithinDirectory(media.Directory, staging.Directory))
                    throw Failure("A media area cannot be nested inside another staging area.");
            }
        }
        return areas.OrderBy(area => area.Directory, PathComparer).ToArray();
    }

    private static bool IsWithinDirectory(string directory, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return PathComparer.Equals(directory, parent) || directory.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    private async Task<FileStream> LockAsync(string file, bool exclusive, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw Failure("Media control files cannot be symbolic links.");
            try
            {
                // Create under the binding lock on subsequent calls; a concurrent creation simply retries.
                if (!File.Exists(file)) using (new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
                return new FileStream(file, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                    exclusive ? FileShare.None : FileShare.Read);
            }
            catch (IOException) when (started.Elapsed < options.Value.LockTimeout)
            {
                await Task.Delay(options.Value.LockRetryDelay, cancellationToken);
            }
            catch (IOException) { throw Failure("Media directories are in use or cannot be locked. Stop their owning hosts before rebinding."); }
        }
    }

    private static bool IsEmpty(string directory, Area[] areas) => Directory.EnumerateFileSystemEntries(directory).All(entry =>
        Path.GetFileName(entry) is UseLockName or BindingLockName || areas.Any(area => PathComparer.Equals(area.Directory, Canonical(entry))));

    private static Binding? ReadMarker(string directory)
    {
        var file = Path.Combine(directory, MarkerName);
        if (!File.Exists(file)) return null;
        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(file).Length > 4096)
            throw Failure("Media ownership marker is invalid.");
        var marker = JsonSerializer.Deserialize<Binding>(File.ReadAllText(file));
        if (marker is null || marker.StoreId == Guid.Empty || !IsHash(marker.Locator) || !IsHash(marker.Directory) ||
            marker.Kind is not ("attachments" or "attachment-staging" or "avatars" or "avatar-staging"))
            throw Failure("Media ownership marker is invalid.");
        return marker;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private void CheckAncestorOwners(string directory, Guid storeId, string locator)
    {
        for (var parent = Directory.GetParent(directory); parent is not null; parent = parent.Parent)
        {
            var marker = ReadMarker(parent.FullName);
            if (marker is null) continue;
            if (marker.Kind.EndsWith("-staging", StringComparison.Ordinal))
                throw Failure("A media area cannot be nested inside another staging area.");
            var sameOwner = marker.StoreId == storeId && marker.Locator == locator;
            var explicitPrevious = options.Value.Action == "rebind" && marker.StoreId == options.Value.PreviousStoreId &&
                marker.Locator == options.Value.PreviousLocator;
            if (!sameOwner && !explicitPrevious) throw Failure("A parent media directory belongs to another database.");
        }
    }

    private static void WriteMarker(string directory, Binding binding)
    {
        var file = Path.Combine(directory, MarkerName);
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, binding);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CheckPath(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (File.Exists(current.FullName) || (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw Failure("Media directory ancestors must be ordinary directories.");
    }

    private static string Canonical(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static MediaBindingException Failure(string message) => new(message);
    private sealed record Area(string Directory, string Kind);
    private sealed record Binding(Guid StoreId, string Locator, string Kind, string Directory);
}

public sealed class MediaBindingException(string message) : Exception(message);
