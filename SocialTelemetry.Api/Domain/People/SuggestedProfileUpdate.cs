using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Domain.People;

public sealed class SuggestedProfileUpdate
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public Guid InteractionAnalysisId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string SuggestedValue { get; set; } = string.Empty;
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;
    public string? AcceptedValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    public Person Person { get; set; } = null!;
    public InteractionAnalysis InteractionAnalysis { get; set; } = null!;
}
