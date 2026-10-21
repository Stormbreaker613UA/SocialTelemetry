namespace SocialTelemetry.Application.Contracts.AI.Models;

public enum AiCapability
{
    Text,
    Vision,
    StructuredOutput
}

public sealed record AiSelectedModel(string ProviderId, string ModelId, AiModelCapabilities CapabilitySupport)
{
    public AiSelectedModel(string providerId, string modelId, IReadOnlyList<AiCapability> capabilities)
        : this(providerId, modelId, AiModelCapabilities.FromSupported(capabilities))
    {
    }

    public IReadOnlyList<AiCapability> Capabilities => CapabilitySupport.SupportedCapabilities;
    [System.Text.Json.Serialization.JsonIgnore]
    public AiExecutionBinding? Binding { get; init; }
    public bool Supports(AiCapability capability) => CapabilitySupport.Supports(capability);
}

public sealed record AiExecutionBinding(Guid UserProfileId, Guid ConnectionId, Guid SelectionGeneration);
