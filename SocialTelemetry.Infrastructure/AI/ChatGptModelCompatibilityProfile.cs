using SocialTelemetry.Application.Contracts.AI.Models;

namespace SocialTelemetry.Infrastructure.AI;

public sealed class ChatGptModelCompatibilityProfile
{
    public string ModelId { get; set; } = "";
    public AiCapabilitySupport Text { get; set; }
    public AiCapabilitySupport Vision { get; set; }
    public AiCapabilitySupport StructuredOutput { get; set; }

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ModelId) && !ModelId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) &&
        Enum.IsDefined(Text) && Enum.IsDefined(Vision) && Enum.IsDefined(StructuredOutput);
}
