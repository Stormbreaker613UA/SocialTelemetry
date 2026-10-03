namespace SocialTelemetry.Api.Domain.Interactions;

public sealed class InteractionAnalysis
{
    public Guid Id { get; set; }
    public Guid InteractionId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public Interaction Interaction { get; set; } = null!;
    public ICollection<AnalysisConversationMessage> ConversationMessages { get; set; } = new List<AnalysisConversationMessage>();
}
