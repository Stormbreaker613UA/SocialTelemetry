using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.People.Create;

public sealed class Input
{
    public Guid UserProfileId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public int? Age { get; init; }
    public string? Gender { get; init; }
    public string? Description { get; init; }
    public RelationshipContext RelationshipContext { get; init; }
    public string? HowWeMet { get; init; }
    public string? Notes { get; init; }
}
