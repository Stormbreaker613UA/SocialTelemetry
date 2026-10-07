namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class LocalAttachmentStorage(
    [FromKeyedServices("attachments")] IFileStorage fileStorage) : IAttachmentStorage
{
    public Task<string> SaveAsync(Stream content, string fileName, string? mimeType, CancellationToken cancellationToken) =>
        fileStorage.SaveAsync(content, cancellationToken);

    public Task<bool> CompleteUploadAsync(string storageKey, CancellationToken cancellationToken) =>
        fileStorage.CompleteUploadAsync(storageKey, cancellationToken);

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken) =>
        fileStorage.ExistsAsync(storageKey, cancellationToken);

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
        fileStorage.OpenReadAsync(storageKey, cancellationToken);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) =>
        fileStorage.DeleteAsync(storageKey, cancellationToken);

    public Task DeleteStagedAsync(string storageKey, CancellationToken cancellationToken) =>
        fileStorage.DeleteStagedAsync(storageKey, cancellationToken);

    public IEnumerable<StoredAttachmentFile> EnumerateFiles() =>
        fileStorage.EnumerateFiles().Select(file => new StoredAttachmentFile(file.StorageKey, file.LastModified));

    public IEnumerable<StoredAttachmentFile> EnumerateStagedFiles() =>
        fileStorage.EnumerateStagedFiles().Select(file => new StoredAttachmentFile(file.StorageKey, file.LastModified));
}
