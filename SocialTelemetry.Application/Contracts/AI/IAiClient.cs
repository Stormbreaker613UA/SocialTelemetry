using SocialTelemetry.Application.Contracts.AI.Models;

namespace SocialTelemetry.Application.Contracts.AI;

public interface IAiClient
{
    Task<AiSelectedModel> GetSelectedModelAsync(CancellationToken cancellationToken);
    Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken);
    Task ValidateExecutionAsync(AiTextResponse result, CancellationToken cancellationToken);
    Task<IAiExecutionLease> AcquireExecutionLeaseAsync(AiTextResponse result, CancellationToken cancellationToken);
}
