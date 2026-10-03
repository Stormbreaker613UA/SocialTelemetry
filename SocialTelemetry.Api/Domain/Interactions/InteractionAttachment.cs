namespace SocialTelemetry.Api.Domain.Interactions;

public sealed class InteractionAttachment
{
    public Guid Id { get; set; }
    public Guid InteractionId { get; set; }
    public AttachmentType Type { get; set; }
    public string? TextContent { get; set; }
    public string? StorageKey { get; set; }
    public string? MimeType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Interaction Interaction { get; set; } = null!;
}
