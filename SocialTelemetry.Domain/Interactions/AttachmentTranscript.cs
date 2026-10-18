namespace SocialTelemetry.Api.Domain.Interactions;

public sealed class AttachmentTranscript
{
    public Guid AttachmentId { get; set; }
    public string? GeneratedText { get; set; }
    public string? CorrectedText { get; set; }
    public TranscriptReviewStatus ReviewStatus { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid Version { get; set; }
    public string SourceStorageKey { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? TranscriptionVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public InteractionAttachment Attachment { get; set; } = null!;
}

public enum TranscriptReviewStatus
{
    Unreviewed,
    Reviewed
}
