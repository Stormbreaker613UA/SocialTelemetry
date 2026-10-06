using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ChatGptPlanAiClient(ChatGptConnection connection, ChatGptHttpClient provider) : IAiClient
{
    private const string ProviderId = "chatgpt-plan";

    public async Task<AiSelectedModel> GetSelectedModelAsync(CancellationToken cancellationToken)
    {
        var session = await connection.GetInferenceSessionAsync(cancellationToken);
        return new AiSelectedModel(ProviderId, session.Model, [AiCapability.Text]);
    }

    public async Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken)
    {
        var session = await connection.GetInferenceSessionAsync(cancellationToken);
        var result = await provider.GenerateAsync(session.AccessToken, session.Model, request, cancellationToken);
        return new AiTextResponse(result.Text, ProviderId, session.Model, result.ReturnedModel);
    }
}
