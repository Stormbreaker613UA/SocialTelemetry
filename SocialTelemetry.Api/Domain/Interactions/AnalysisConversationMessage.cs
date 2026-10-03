namespace SocialTelemetry.Api.Domain.Interactions;

public sealed class AnalysisConversationMessage
{
    public Guid Id { get; set; }
    public Guid InteractionAnalysisId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public InteractionAnalysis InteractionAnalysis { get; set; } = null!;
}
