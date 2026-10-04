namespace SocialTelemetry.Api.Infrastructure.AI.Models;

public sealed record InteractionAnalysisResult(string Summary)
{
    public string? Translation { get; init; }
    public string? LiteralMeaning { get; init; }
    public string? SocialMeaning { get; init; }
    public IReadOnlyList<AnalysisInterpretation> Interpretations { get; init; } = [];
    public IReadOnlyList<string> Uncertainties { get; init; } = [];
    public IReadOnlyList<string> MissingContext { get; init; } = [];
    public IReadOnlyList<string> SuggestedReplies { get; init; } = [];
    public IReadOnlyList<string> SuggestedNextSteps { get; init; } = [];
    public IReadOnlyList<SuggestedProfileUpdate> SuggestedProfileUpdates { get; init; } = [];
}

public sealed record AnalysisInterpretation(
    string Meaning,
    decimal? Confidence,
    IReadOnlyList<Guid> SupportingInteractionIds);
