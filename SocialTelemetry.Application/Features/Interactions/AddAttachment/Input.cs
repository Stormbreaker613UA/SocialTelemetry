using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.AddAttachment;

public sealed record Input(Guid InteractionId, AttachmentType Type, string? TextContent, UploadFile? File);

// Opening is deferred until validation and the owned parent lookup have succeeded.
public sealed record UploadFile(long Length, string FileName, string? MimeType, Func<Stream> OpenRead);
