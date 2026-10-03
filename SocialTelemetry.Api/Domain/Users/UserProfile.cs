using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;

namespace SocialTelemetry.Api.Domain.Users;

public sealed class UserProfile
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? AboutMe { get; set; }
    public string? CommunicationStyle { get; set; }
    public string? Goals { get; set; }
    public string? Preferences { get; set; }
    public string? Boundaries { get; set; }
    public string? AiInstructions { get; set; }

    public ICollection<Person> People { get; set; } = new List<Person>();
    public ICollection<Interaction> Interactions { get; set; } = new List<Interaction>();
}
