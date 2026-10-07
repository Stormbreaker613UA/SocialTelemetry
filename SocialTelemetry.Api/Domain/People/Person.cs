using SocialTelemetry.Api.Domain.Users;

namespace SocialTelemetry.Api.Domain.People;

public sealed class Person
{
    public Guid Id { get; set; }
    public Guid UserProfileId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string? Gender { get; set; }
    public string? Description { get; set; }
    public RelationshipContext RelationshipContext { get; set; }
    public string? HowWeMet { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? AvatarStorageKey { get; set; }
    public string? AvatarMimeType { get; set; }

    public UserProfile UserProfile { get; set; } = null!;
    public ICollection<PersonFact> Facts { get; set; } = new List<PersonFact>();
    public ICollection<PersonInference> Inferences { get; set; } = new List<PersonInference>();
}
