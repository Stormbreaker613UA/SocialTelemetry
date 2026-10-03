namespace SocialTelemetry.Api.Features.Interactions.Create;

public sealed class Request
{
    public Guid UserProfileId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? UserThoughts { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public IReadOnlyList<Guid> ParticipantIds { get; init; } = [];
}
