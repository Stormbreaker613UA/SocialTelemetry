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

        Directory.CreateDirectory(storageDirectory);

        var storageKey = Guid.NewGuid().ToString("N");
        var filePath = Path.GetFullPath(Path.Combine(storageDirectory, storageKey));

        if (!IsWithinStorageDirectory(filePath))
        {
            throw new InvalidOperationException("The generated attachment path is outside the local storage directory.");
        }

        await using var destination = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await content.CopyToAsync(destination, cancellationToken);

        return storageKey;
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var filePath = GetFilePath(storageKey);

        if (!File.Exists(filePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream content = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return Task.FromResult<Stream?>(content);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        var filePath = GetFilePath(storageKey);
        File.Delete(filePath);

        return Task.CompletedTask;
    }

    private string GetFilePath(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var filePath = Path.GetFullPath(Path.Combine(storageDirectory, storageKey));

        if (!IsWithinStorageDirectory(filePath))
        {
            throw new InvalidOperationException("The attachment path is outside the local storage directory.");
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
