using SocialTelemetry.Api.Domain.People;

namespace SocialTelemetry.Api.Features.People.GetAll;

public sealed record Response(IReadOnlyList<PersonResponse> People);

public sealed record PersonResponse(
    Guid Id,
    string DisplayName,
    int? Age,
    string? Gender,
    string? Description,
    RelationshipContext RelationshipContext,
    string? HowWeMet,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt = null);
