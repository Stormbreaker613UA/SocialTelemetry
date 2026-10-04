namespace SocialTelemetry.Api.Domain.Interactions;

public sealed class InteractionAnalysis
{
    public Guid Id { get; set; }
    public Guid InteractionId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? SchemaVersion { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Interaction Interaction { get; set; } = null!;
    public ICollection<AnalysisConversationMessage> ConversationMessages { get; set; } = new List<AnalysisConversationMessage>();
}
