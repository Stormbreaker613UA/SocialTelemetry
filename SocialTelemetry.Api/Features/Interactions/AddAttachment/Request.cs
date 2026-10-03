using Microsoft.AspNetCore.Http;
using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Features.Interactions.AddAttachment;

public sealed class Request
{
    public Guid InteractionId { get; init; }
    public AttachmentType Type { get; init; }
    public string? TextContent { get; init; }
    public IFormFile? File { get; init; }
}
