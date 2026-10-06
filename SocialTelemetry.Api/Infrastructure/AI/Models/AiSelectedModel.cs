namespace SocialTelemetry.Api.Infrastructure.AI.Models;

public enum AiCapability
{
    Text,
    Vision,
    StructuredOutput
}

public sealed record AiSelectedModel(string ProviderId, string ModelId, IReadOnlyList<AiCapability> Capabilities)
{
    public bool Supports(AiCapability capability) => Capabilities.Contains(capability);
}
