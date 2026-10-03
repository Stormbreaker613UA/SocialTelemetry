namespace SocialTelemetry.Api.Infrastructure.Storage;

public interface IAttachmentStorage
{
    Task<string> SaveAsync(Stream content, string fileName, string? mimeType, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}
