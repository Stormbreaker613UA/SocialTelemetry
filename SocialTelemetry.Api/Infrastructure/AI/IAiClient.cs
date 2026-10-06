using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Infrastructure.AI;

public interface IAiClient
{
    Task<AiSelectedModel> GetSelectedModelAsync(CancellationToken cancellationToken);
    Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken);
    Task ValidateExecutionAsync(AiTextResponse result, CancellationToken cancellationToken);
}
