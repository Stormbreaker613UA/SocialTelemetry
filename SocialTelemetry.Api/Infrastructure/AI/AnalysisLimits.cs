namespace SocialTelemetry.Api.Infrastructure.AI;

public static class AnalysisLimits
{
    public const int Participants = 10;
    public const int FactsPerPerson = 20;
    public const int InferencesPerPerson = 10;
    public const int PreviousInteractions = 5;
    public const int EvidenceCount = 10;
    public const int ImageCount = 3;
    public const int ImageBytes = 5 * 1024 * 1024;
    public const int TotalImageBytes = 10 * 1024 * 1024;
    public const int TextEvidenceCharacters = 12_000;
    public const int ContextCharacters = 64_000;
    public const int QuestionCharacters = 2_000;
    public const int ResultCharacters = 128_000;
}
