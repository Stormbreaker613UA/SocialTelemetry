using FastEndpoints;

namespace SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;

public sealed record Request
{
    [RouteParam]
    public Guid InteractionId { get; init; }
    public string? UserQuestion { get; init; }
    // null selects all Ready evidence; [] explicitly selects no attachments.
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
}
