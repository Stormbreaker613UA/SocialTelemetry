namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class LocalAttachmentStorage : IAttachmentStorage
{
    public Task<string> SaveAsync(Stream content, string fileName, string? mimeType, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
