using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;

public sealed record Response(Guid Id, Guid InteractionId, InteractionAnalysisResult Result,
    string Provider, string Model, string RequestedModel, string? ReturnedModel,
    string SchemaVersion, string PromptVersion, DateTimeOffset CreatedAt);
