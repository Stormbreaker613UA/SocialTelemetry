using SocialTelemetry.Api.Domain.People;

namespace SocialTelemetry.Api.Features.People.Update;

public sealed class Request
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public int? Age { get; init; }
    public string? Gender { get; init; }
    public string? Description { get; init; }
    public RelationshipContext RelationshipContext { get; init; }
    public string? HowWeMet { get; init; }
    public string? Notes { get; init; }
}
