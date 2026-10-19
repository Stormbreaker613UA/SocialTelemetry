namespace SocialTelemetry.Application.Features.Interactions.Update;

public sealed class Input
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? UserThoughts { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public IReadOnlyList<Guid> ParticipantIds { get; init; } = [];
}
