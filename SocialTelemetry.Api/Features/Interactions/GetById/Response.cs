namespace SocialTelemetry.Api.Features.Interactions.GetById;

public sealed record Response(
    Guid Id,
    Guid UserProfileId,
    string Title,
    string Description,
    string? UserThoughts,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ParticipantResponse> Participants);

public sealed record ParticipantResponse(Guid Id, string DisplayName);
