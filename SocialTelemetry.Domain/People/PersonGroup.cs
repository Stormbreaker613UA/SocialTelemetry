using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Domain.People;

public sealed class PersonGroup
{
    public const int MaximumNameLength = 200;
    public Guid Id { get; set; }
    public Guid UserProfileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public UserProfile UserProfile { get; set; } = null!;
}
