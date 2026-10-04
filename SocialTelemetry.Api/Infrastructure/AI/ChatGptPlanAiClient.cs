using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ChatGptPlanAiClient(ChatGptConnection connection, ChatGptHttpClient provider) : IAiClient
{
    public async Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken)
    {
        var session = await connection.GetInferenceSessionAsync(cancellationToken);
        return await provider.GenerateAsync(session.AccessToken, session.Model, request, cancellationToken);
    }
}
