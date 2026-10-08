using FastEndpoints;

namespace SocialTelemetry.Api.Features.Interactions.GetTranscript;

public sealed record Request
{
    [RouteParam] public Guid InteractionId { get; init; }
    [RouteParam] public Guid AttachmentId { get; init; }
}
