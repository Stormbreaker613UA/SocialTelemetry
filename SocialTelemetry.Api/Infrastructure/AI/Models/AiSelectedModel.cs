namespace SocialTelemetry.Api.Infrastructure.AI.Models;

public enum AiCapability
{
    Text,
    Vision,
    StructuredOutput
}

public sealed record AiSelectedModel(string ProviderId, string ModelId, IReadOnlyList<AiCapability> Capabilities)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public AiExecutionBinding? Binding { get; init; }
    public bool Supports(AiCapability capability) => Capabilities.Contains(capability);
}

public sealed record AiExecutionBinding(Guid UserProfileId, Guid ConnectionId, Guid SelectionGeneration);
