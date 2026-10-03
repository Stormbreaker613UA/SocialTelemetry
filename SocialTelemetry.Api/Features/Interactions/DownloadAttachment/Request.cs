namespace SocialTelemetry.Api.Features.Interactions.DownloadAttachment;

public sealed class Request
{
    public Guid InteractionId { get; init; }
    public Guid AttachmentId { get; init; }
}
