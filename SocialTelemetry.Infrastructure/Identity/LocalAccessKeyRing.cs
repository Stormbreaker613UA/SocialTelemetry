using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Runtime;

namespace SocialTelemetry.Infrastructure.Identity;

// Delegates cryptography to the framework. Initialization follows database/media binding.
public sealed class LocalAccessKeyRing(IServiceScopeFactory scopes, ApplicationPaths paths,
    IOptions<LocalAccessOptions> options) : IDataProtectionProvider, IHostedService
{
    private IDataProtectionProvider? provider;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storeId = await database.StorageDatabaseIdentities.AsNoTracking().Select(row => row.StoreId).SingleAsync(cancellationToken);
        var root = paths.ResolveCredentialDirectory(options.Value.DataDirectory);
        var directory = Path.Combine(root, storeId.ToString("N"), "keys");
        try
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The key location is not a regular directory.");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else
            {
                Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.CreateDirectory(Path.GetDirectoryName(directory)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var ownedDirectory in new[] { root, Path.GetDirectoryName(directory)!, directory })
                    if ((File.GetUnixFileMode(ownedDirectory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw new IOException();
            }
            provider = DataProtectionProvider.Create(new DirectoryInfo(directory), builder =>
            {
                builder.SetApplicationName("SocialTelemetry.LocalAccess." + storeId.ToString("N"));
                if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            throw new LocalAccessException(LocalAccessFailure.StorageUnavailable);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public IDataProtector CreateProtector(string purpose) => new DeferredProtector(this, [purpose]);

    private sealed class DeferredProtector(LocalAccessKeyRing owner, string[] purposes) : IDataProtector
    {
        private IDataProtector Resolve()
        {
            var current = owner.provider ?? throw new LocalAccessException(LocalAccessFailure.StorageUnavailable);
            IDataProtector? protector = null;
            foreach (var purpose in purposes) { protector = current.CreateProtector(purpose); current = protector; }
            return protector!;
        }
        public IDataProtector CreateProtector(string purpose) => new DeferredProtector(owner, [.. purposes, purpose]);
        public byte[] Protect(byte[] plaintext) => Resolve().Protect(plaintext);
        public byte[] Unprotect(byte[] protectedData) => Resolve().Unprotect(protectedData);
    }
}
