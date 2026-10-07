using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.Runtime;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ChatGptCredentialStore(IOptions<ChatGptOptions> options, ApplicationPaths paths)
{
    private readonly string directory = paths.ResolveCredentialDirectory(options.Value.DataDirectory);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IDataProtector? protector;

    internal async Task<IDisposable> LockAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.CredentialLockTimeout);
        var entered = false;
        try
        {
            await gate.WaitAsync(timeout.Token);
            entered = true;
            EnsureDirectory();
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    var file = new FileStream(Path.Combine(directory, "chatgpt-state.lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new StoreLock(file, gate);
                }
                catch (IOException)
                {
                    await Task.Delay(options.Value.CredentialLockRetryDelay, timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (entered) gate.Release();
            throw new AiProviderException(AiFailure.ConnectionBusy);
        }
        catch
        {
            if (entered) gate.Release();
            throw;
        }
    }

    internal async Task<ChatGptState> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(directory, "chatgpt-state.bin");
            if (!File.Exists(path)) return new ChatGptState();
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
            var plaintext = GetProtector().Unprotect(encrypted);
            try
            {
                return JsonSerializer.Deserialize<ChatGptState>(plaintext)
                    ?? throw new AiProviderException(AiFailure.StorageUnavailable);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            throw new AiProviderException(AiFailure.StorageUnavailable);
        }
    }

    internal async Task SaveAsync(ChatGptState state, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(directory, "chatgpt-state.bin." + Guid.NewGuid().ToString("N"));
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
            byte[] encrypted;
            try { encrypted = GetProtector().Protect(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, Path.Combine(directory, "chatgpt-state.bin"), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new AiProviderException(AiFailure.StorageUnavailable);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void EnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AiProviderException(AiFailure.StorageUnavailable);
        }
    }

    private IDataProtector GetProtector()
    {
        if (protector is not null) return protector;
        var keyDirectory = new DirectoryInfo(Path.Combine(directory, "chatgpt-keys"));
        var provider = DataProtectionProvider.Create(keyDirectory, builder =>
        {
            builder.SetApplicationName("SocialTelemetry.ChatGpt");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        protector = provider.CreateProtector("OAuthCredentials.v1");
        return protector;
    }

    private sealed class StoreLock(FileStream file, SemaphoreSlim gate) : IDisposable
    {
        public void Dispose()
        {
            try { file.Dispose(); }
            finally { gate.Release(); }
        }
    }
}
