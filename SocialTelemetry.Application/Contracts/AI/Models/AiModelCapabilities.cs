namespace SocialTelemetry.Application.Contracts.AI.Models;

public enum AiCapabilitySupport
{
    Unknown,
    Supported,
    Unsupported
}

public sealed record AiModelCapabilities(
    AiCapabilitySupport Text = AiCapabilitySupport.Unknown,
    AiCapabilitySupport Vision = AiCapabilitySupport.Unknown,
    AiCapabilitySupport StructuredOutput = AiCapabilitySupport.Unknown)
{
    public AiCapabilitySupport GetSupport(AiCapability capability) => capability switch
    {
        AiCapability.Text => Text,
        AiCapability.Vision => Vision,
        AiCapability.StructuredOutput => StructuredOutput,
        _ => AiCapabilitySupport.Unknown
    };

    public bool Supports(AiCapability capability) => GetSupport(capability) == AiCapabilitySupport.Supported;

    public IReadOnlyList<AiCapability> SupportedCapabilities => Array.AsReadOnly(
        Enum.GetValues<AiCapability>().Where(Supports).ToArray());

    public static AiModelCapabilities FromSupported(IReadOnlyList<AiCapability> capabilities) => new(
        capabilities.Contains(AiCapability.Text) ? AiCapabilitySupport.Supported : AiCapabilitySupport.Unknown,
        capabilities.Contains(AiCapability.Vision) ? AiCapabilitySupport.Supported : AiCapabilitySupport.Unknown,
        capabilities.Contains(AiCapability.StructuredOutput) ? AiCapabilitySupport.Supported : AiCapabilitySupport.Unknown);
}
