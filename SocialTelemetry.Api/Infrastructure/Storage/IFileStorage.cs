namespace SocialTelemetry.Api.Infrastructure.Storage;

public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, CancellationToken cancellationToken);
    Task<bool> CompleteUploadAsync(string storageKey, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteStagedAsync(string storageKey, CancellationToken cancellationToken);
    IEnumerable<StoredFile> EnumerateFiles();
    IEnumerable<StoredFile> EnumerateStagedFiles();
}

public sealed record StoredFile(string StorageKey, DateTimeOffset LastModified);
