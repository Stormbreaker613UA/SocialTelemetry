using SocialTelemetry.Application.Contracts.AI.Models;

namespace SocialTelemetry.Application.Features.Analysis.AnalyzeInteraction;

public sealed record Result(Guid Id, Guid InteractionId, InteractionAnalysisResult Analysis,
    string Provider, string Model, string RequestedModel, string? ReturnedModel,
    string SchemaVersion, string PromptVersion, DateTimeOffset CreatedAt);
