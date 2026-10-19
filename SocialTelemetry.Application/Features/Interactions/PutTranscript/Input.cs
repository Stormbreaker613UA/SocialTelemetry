namespace SocialTelemetry.Application.Features.Interactions.PutTranscript;

public sealed record Input
{
    public Guid InteractionId { get; init; }
    public Guid AttachmentId { get; init; }
    // Null is creation-only. Every subsequent edit/review must name the version read by the user.
    public Guid? ExpectedVersion { get; init; }
    public string? CorrectedText { get; init; }
    public bool ConfirmReviewed { get; init; }
}
