using FastEndpoints;

namespace SocialTelemetry.Api.Features.Interactions.PutTranscript;

public sealed record Request
{
    [RouteParam] public Guid InteractionId { get; init; }
    [RouteParam] public Guid AttachmentId { get; init; }
    // Null is creation-only. Every subsequent edit/review must name the version read by the user.
    public Guid? ExpectedVersion { get; init; }
    public string? CorrectedText { get; init; }
    public bool ConfirmReviewed { get; init; }
}
