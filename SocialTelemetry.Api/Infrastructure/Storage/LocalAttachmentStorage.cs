namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class LocalAttachmentStorage : IAttachmentStorage
{
    private readonly string storageDirectory;
    private readonly string stagingDirectory;
    private readonly ILogger<LocalAttachmentStorage> logger;

    public LocalAttachmentStorage(IConfiguration configuration, IWebHostEnvironment webHostEnvironment, ILogger<LocalAttachmentStorage> logger)
    {
        var configuredDirectory = configuration["AttachmentStorage:LocalDirectory"];
        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? "attachments"
            : configuredDirectory;

        storageDirectory = Path.GetFullPath(directory, webHostEnvironment.ContentRootPath);
        stagingDirectory = Path.Combine(storageDirectory, ".staging");
        this.logger = logger;
    }

    public async Task<string> SaveAsync(
        Stream content,
        string fileName,
        string? mimeType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureSafeDirectories();
        Directory.CreateDirectory(stagingDirectory);

        var storageKey = Guid.NewGuid().ToString("N");
        var filePath = GetFilePath(storageKey, staged: true);

        if (!IsWithinStorageDirectory(filePath))
        {
            throw new InvalidOperationException("The generated attachment path is outside the local storage directory.");
        }

        var destination = new FileStream(
            filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        try
        {
            await using (destination)
            {
                await content.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
        }
        catch
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning("Staged file cleanup failed for {StorageKey} ({ExceptionType})",
                    storageKey, cleanupException.GetType().Name);
            }
            throw;
        }

        return storageKey;
    }

    public Task<bool> CompleteUploadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var finalPath = GetFilePath(storageKey);
        var stagedPath = GetFilePath(storageKey, staged: true);
        if (File.Exists(finalPath))
        {
            DeleteFile(stagedPath);
            return Task.FromResult(true);
        }

        if (!File.Exists(stagedPath))
        {
            return Task.FromResult(false);
        }

        // Both paths share a storage root so the rename stays on the same volume.
        Directory.CreateDirectory(storageDirectory);
        File.Move(stagedPath, finalPath);
        return Task.FromResult(true);
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetFilePath(storageKey)));
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filePath = GetFilePath(storageKey);
        try
        {
            Stream content = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            return Task.FromResult<Stream?>(content);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteFile(GetFilePath(storageKey));
        return Task.CompletedTask;
    }

    public Task DeleteStagedAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteFile(GetFilePath(storageKey, staged: true));
        return Task.CompletedTask;
    }

    public IEnumerable<StoredAttachmentFile> EnumerateFiles() => EnumerateFiles(staged: false);
    public IEnumerable<StoredAttachmentFile> EnumerateStagedFiles() => EnumerateFiles(staged: true);

    private IEnumerable<StoredAttachmentFile> EnumerateFiles(bool staged)
    {
        EnsureSafeDirectories();
        var directory = staged ? stagingDirectory : storageDirectory;
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var filePath in Directory.EnumerateFiles(directory))
        {
            var storageKey = Path.GetFileName(filePath);
            if (!Guid.TryParseExact(storageKey, "N", out _) ||
                File.GetAttributes(filePath).HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            yield return new StoredAttachmentFile(storageKey, File.GetLastWriteTimeUtc(filePath));
        }
    }

    private static void DeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (DirectoryNotFoundException)
        {
            // Deletion is idempotent even when the storage directory is already gone.
        }
    }

    private string GetFilePath(string storageKey, bool staged = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        if (!Guid.TryParseExact(storageKey, "N", out _))
        {
            throw new InvalidOperationException("The attachment storage key is invalid.");
        }

        EnsureSafeDirectories();
        var directory = staged ? stagingDirectory : storageDirectory;
        var filePath = Path.GetFullPath(Path.Combine(directory, storageKey));

        if (!IsWithinStorageDirectory(filePath))
        {
            throw new InvalidOperationException("The attachment path is outside the local storage directory.");
        }

        if (File.Exists(filePath) && File.GetAttributes(filePath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Attachment storage does not support symbolic links.");
        }

        return filePath;
    }

    private void EnsureSafeDirectories()
    {
        foreach (var directory in new[] { storageDirectory, stagingDirectory })
        {
            if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Attachment storage directories cannot be symbolic links.");
            }
        }
    }

    private bool IsWithinStorageDirectory(string filePath)
    {
        var relativePath = Path.GetRelativePath(storageDirectory, filePath);

        return !Path.IsPathRooted(relativePath) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               relativePath != "..";
    }
}
