using SocialTelemetry.Api.Domain.People;

namespace SocialTelemetry.Api.Features.People.Update;

public sealed record Response(
    Guid Id,
    Guid UserProfileId,
    string DisplayName,
    int? Age,
    string? Gender,
    string? Description,
    RelationshipContext RelationshipContext,
    string? HowWeMet,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
