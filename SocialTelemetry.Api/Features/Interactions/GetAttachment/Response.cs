using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Features.Interactions.GetAttachment;

public sealed record Response(
    Guid Id,
    Guid InteractionId,
    AttachmentType Type,
    string? MimeType,
    string? TextContent,
    DateTimeOffset CreatedAt);
