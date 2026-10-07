using System.ComponentModel.DataAnnotations;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class AnalysisOptions
{
    [Range(1, 50)] public int Participants { get; set; } = 10;
    [Range(0, 100)] public int FactsPerPerson { get; set; } = 20;
    [Range(0, 50)] public int InferencesPerPerson { get; set; } = 10;
    [Range(0, 20)] public int PreviousInteractions { get; set; } = 5;
    [Range(1, 50)] public int EvidenceCount { get; set; } = 10;
    [Range(1, 10)] public int ImageCount { get; set; } = 3;
    [Range(1, 20 * 1024 * 1024)] public int ImageBytes { get; set; } = 5 * 1024 * 1024;
    [Range(1, 50 * 1024 * 1024)] public int TotalImageBytes { get; set; } = 10 * 1024 * 1024;
    [Range(1, 64_000)] public int TextEvidenceCharacters { get; set; } = 12_000;
    [Range(1, 512_000)] public int ContextCharacters { get; set; } = 64_000;
    [Range(1, 8_000)] public int QuestionCharacters { get; set; } = 2_000;
    [Range(4_000, 512_000)] public int ResultCharacters { get; set; } = 128_000;
    [Range(1, 2_000)] public int InteractionTitleCharacters { get; set; } = 500;
    [Range(1, 32_000)] public int InteractionDescriptionCharacters { get; set; } = 8_000;
    [Range(1, 16_000)] public int UserThoughtsCharacters { get; set; } = 4_000;
    [Range(1, 16_000)] public int ProfileFieldCharacters { get; set; } = 4_000;
    [Range(1, 8_000)] public int FactValueCharacters { get; set; } = 2_000;
    [Range(1, 4_000)] public int FactSourceCharacters { get; set; } = 1_000;
    [Range(1, 8_000)] public int InferenceValueCharacters { get; set; } = 2_000;

    public bool HasConsistentLimits() => TotalImageBytes >= ImageBytes && ImageCount <= EvidenceCount &&
        new[] { TextEvidenceCharacters, QuestionCharacters, InteractionTitleCharacters,
            InteractionDescriptionCharacters, UserThoughtsCharacters, ProfileFieldCharacters,
            FactValueCharacters, FactSourceCharacters, InferenceValueCharacters }
        .All(limit => limit <= ContextCharacters);
}
