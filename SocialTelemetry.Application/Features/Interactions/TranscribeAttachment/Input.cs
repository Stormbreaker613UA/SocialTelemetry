namespace SocialTelemetry.Application.Features.Interactions.TranscribeAttachment;

public sealed record Input
{
    public Guid InteractionId { get; init; }
    public Guid AttachmentId { get; init; }
}
