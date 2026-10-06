using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ChatGptPlanAiClient(ChatGptConnection connection, ChatGptHttpClient provider) : IAiClient
{
    private const string ProviderId = "chatgpt-plan";

    public async Task<AiSelectedModel> GetSelectedModelAsync(CancellationToken cancellationToken)
    {
        var session = await connection.GetInferenceSessionAsync(cancellationToken);
        return new AiSelectedModel(ProviderId, session.Model, Capabilities(session.Model));
    }

    private static IReadOnlyList<AiCapability> Capabilities(string model)
    {
        // The plan catalog documents no capability fields. Only these exact documented IDs
        // have confirmed vision/structured output: developers.openai.com/api/docs/models/gpt-5.6-sol
        return model is "gpt-5.6-sol" or "gpt-5.6"
            ? [AiCapability.Text, AiCapability.Vision, AiCapability.StructuredOutput]
            : [AiCapability.Text];
    }

    public async Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken)
    {
        var session = await connection.GetInferenceSessionAsync(cancellationToken);
        if (request.SelectedModel is { } selected &&
            (selected.ProviderId != ProviderId || selected.ModelId != session.Model))
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        var capabilities = Capabilities(session.Model);
        if ((request.Images.Count > 0 && !capabilities.Contains(AiCapability.Vision)) ||
            (request.StructuredOutput is not null && !capabilities.Contains(AiCapability.StructuredOutput)))
            throw new AiProviderException(AiFailure.CapabilityMissing);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.LifetimeCancellationToken);
        try
        {
            var result = await provider.GenerateAsync(session.AccessToken, session.Model, request, execution.Token);
            var completed = new AiTextResponse(result.Text, ProviderId, session.Model, result.ReturnedModel)
            {
                ExecutionId = session.SessionId,
                LifetimeCancellationToken = session.LifetimeCancellationToken
            };
            await ValidateExecutionAsync(completed, cancellationToken);
            return completed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && session.LifetimeCancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        }
    }

    public async Task ValidateExecutionAsync(AiTextResponse result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.LifetimeCancellationToken.IsCancellationRequested)
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        await connection.ValidateSessionAsync(result.ExecutionId, cancellationToken);
        if (result.LifetimeCancellationToken.IsCancellationRequested)
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
    }
}
