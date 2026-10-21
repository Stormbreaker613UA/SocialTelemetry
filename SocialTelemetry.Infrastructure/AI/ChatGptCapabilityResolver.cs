using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.AI.Models;

namespace SocialTelemetry.Infrastructure.AI;

public sealed class ChatGptCapabilityResolver(IOptions<ChatGptOptions> options)
{
    // Compatibility assertions never supply or filter the account's live provider catalog.
    private readonly IReadOnlyDictionary<string, AiModelCapabilities> profiles = options.Value.CompatibilityProfiles
        .ToDictionary(profile => profile.ModelId,
            profile => new AiModelCapabilities(profile.Text, profile.Vision, profile.StructuredOutput), StringComparer.Ordinal);

    public AiModelCapabilities Resolve(string modelId) =>
        profiles.TryGetValue(modelId, out var capabilities) ? capabilities : new AiModelCapabilities();
}
