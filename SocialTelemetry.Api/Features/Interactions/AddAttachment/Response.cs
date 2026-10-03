using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Features.Interactions.AddAttachment;

public sealed record Response(
    Guid Id,
    Guid InteractionId,
    AttachmentType Type,
    string? TextContent,
    string? StorageKey,
    string? MimeType,
    DateTimeOffset CreatedAt);
