namespace SocialTelemetry.Api.Infrastructure.AI.Models;

public sealed record InteractionAnalysisResult(string Summary)
{
    public string? Translation { get; init; }
    public string? LiteralMeaning { get; init; }
    public string? SocialMeaning { get; init; }
    public string? Tone { get; init; }
    public IReadOnlyList<string> ObservedFacts { get; init; } = [];
    public IReadOnlyList<string> WhatUserDidWell { get; init; } = [];
    public IReadOnlyList<string> PossibleMissedSignals { get; init; } = [];
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
    IReadOnlyList<Guid> SupportingInteractionIds)
{
    public IReadOnlyList<Guid> PersonIds { get; init; } = [];
    public IReadOnlyList<Guid> SupportingEvidenceIds { get; init; } = [];
    public IReadOnlyList<Guid> SupportingFactIds { get; init; } = [];
    public IReadOnlyList<Guid> SupportingInferenceIds { get; init; } = [];
}
