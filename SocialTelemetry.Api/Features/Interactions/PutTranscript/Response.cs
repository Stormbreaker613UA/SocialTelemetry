using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Features.Interactions.PutTranscript;

public sealed record Response(Guid AttachmentId, string? GeneratedText, string? CorrectedText, string EffectiveText,
    TranscriptReviewStatus ReviewStatus, DateTimeOffset? ReviewedAt, Guid Version,
    string SourceSha256, string? Provider, string? Model, string? TranscriptionVersion,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
