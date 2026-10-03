namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class LocalAttachmentStorage : IAttachmentStorage
{
    private readonly string storageDirectory;

    public LocalAttachmentStorage(IConfiguration configuration, IWebHostEnvironment webHostEnvironment)
    {
        var configuredDirectory = configuration["AttachmentStorage:LocalDirectory"];
        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? "attachments"
            : configuredDirectory;

        storageDirectory = Path.GetFullPath(directory, webHostEnvironment.ContentRootPath);
    }

    public async Task<string> SaveAsync(
        Stream content,
        string fileName,
        string? mimeType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(storageDirectory);

        var storageKey = Guid.NewGuid().ToString("N");
        var filePath = Path.GetFullPath(Path.Combine(storageDirectory, storageKey));

        if (!IsWithinStorageDirectory(filePath))
        {
            throw new InvalidOperationException("The generated attachment path is outside the local storage directory.");
        }

        await using var destination = new FileStream(
            filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        try
        {
            await content.CopyToAsync(destination, cancellationToken);
        }
        catch
        {
            await destination.DisposeAsync();
            File.Delete(filePath);
            throw;
        }

        return storageKey;
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
        var filePath = GetFilePath(storageKey);
        try
        {
            File.Delete(filePath);
        }
        catch (DirectoryNotFoundException)
        {
            // Deletion is idempotent even when the storage directory is already gone.
        }

        return Task.CompletedTask;
    }

    private string GetFilePath(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        if (!Guid.TryParseExact(storageKey, "N", out _))
        {
            throw new InvalidOperationException("The attachment storage key is invalid.");
        }

        var filePath = Path.GetFullPath(Path.Combine(storageDirectory, storageKey));

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

    private bool IsWithinStorageDirectory(string filePath)
    {
        var relativePath = Path.GetRelativePath(storageDirectory, filePath);

        return !Path.IsPathRooted(relativePath) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               relativePath != "..";
    }
}
