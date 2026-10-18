using SocialTelemetry.Api.Domain.Users;

namespace SocialTelemetry.Api.Domain.Interactions;

public sealed class Interaction
{
    public Guid Id { get; set; }
    public Guid UserProfileId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? UserThoughts { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public UserProfile UserProfile { get; set; } = null!;
    public ICollection<InteractionParticipant> Participants { get; set; } = new List<InteractionParticipant>();
    public ICollection<InteractionAttachment> Attachments { get; set; } = new List<InteractionAttachment>();
    public ICollection<InteractionAnalysis> Analyses { get; set; } = new List<InteractionAnalysis>();
}
