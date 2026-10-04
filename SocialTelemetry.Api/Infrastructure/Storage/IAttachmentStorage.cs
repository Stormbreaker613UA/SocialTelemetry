namespace SocialTelemetry.Api.Infrastructure.Storage;

public interface IAttachmentStorage
{
    Task<string> SaveAsync(Stream content, string fileName, string? mimeType, CancellationToken cancellationToken);
    Task<bool> CompleteUploadAsync(string storageKey, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteStagedAsync(string storageKey, CancellationToken cancellationToken);
    IEnumerable<StoredAttachmentFile> EnumerateFiles();
    IEnumerable<StoredAttachmentFile> EnumerateStagedFiles();
}

public sealed record StoredAttachmentFile(string StorageKey, DateTimeOffset LastModified);
