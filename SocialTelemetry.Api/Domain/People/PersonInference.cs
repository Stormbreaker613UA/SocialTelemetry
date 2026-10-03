using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Domain.People;

public sealed class PersonInference
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string Value { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public Guid? SourceInteractionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Person Person { get; set; } = null!;
    public Interaction? SourceInteraction { get; set; }
}
