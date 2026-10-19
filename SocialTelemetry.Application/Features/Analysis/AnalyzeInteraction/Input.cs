namespace SocialTelemetry.Application.Features.Analysis.AnalyzeInteraction;

public sealed record Input(Guid InteractionId, string? UserQuestion, IReadOnlyList<Guid>? AttachmentIds);
