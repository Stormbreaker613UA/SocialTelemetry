using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class OpenAiClient : IAiClient
{
    public Task<InteractionAnalysisResult> AnalyzeInteractionAsync(string context, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<string> AskAboutPersonAsync(string context, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<string> AskFollowUpAsync(string context, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
