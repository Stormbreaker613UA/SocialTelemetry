using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Infrastructure.AI;

public interface IAiClient
{
    Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken);
}
