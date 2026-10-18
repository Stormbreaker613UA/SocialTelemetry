using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Domain.Interactions;

public sealed class InteractionParticipant
{
    public Guid InteractionId { get; set; }
    public Guid PersonId { get; set; }

    public Interaction Interaction { get; set; } = null!;
    public Person Person { get; set; } = null!;
}
