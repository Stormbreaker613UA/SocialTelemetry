using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Infrastructure.AI;

public interface IAiClient
{
    Task<InteractionAnalysisResult> AnalyzeInteractionAsync(string context, CancellationToken cancellationToken);
    Task<string> AskAboutPersonAsync(string context, CancellationToken cancellationToken);
    Task<string> AskFollowUpAsync(string context, CancellationToken cancellationToken);
}
