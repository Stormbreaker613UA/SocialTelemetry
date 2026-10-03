namespace SocialTelemetry.Api.Features.Interactions.GetForPerson;

public sealed record Response(IReadOnlyList<InteractionResponse> Interactions);

public sealed record InteractionResponse(
    Guid Id,
    string Title,
    string Description,
    string? UserThoughts,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt);
