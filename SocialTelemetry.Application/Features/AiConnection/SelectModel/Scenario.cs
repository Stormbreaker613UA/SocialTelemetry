using SocialTelemetry.Application.Contracts.AI;

namespace SocialTelemetry.Application.Features.AiConnection.SelectModel;

public sealed class Scenario(IProfileAiManagement connection)
{
    public Task ExecuteAsync(string modelId, CancellationToken cancellationToken)
    {
        ValidateInput(modelId);
        return connection.SelectModelAsync(modelId, cancellationToken);
    }

    public static void ValidateInput(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 200)
            throw new AiProviderException(AiFailure.ModelUnavailable);
    }

    public static bool ShouldChange(string modelId, string? selectedModel, IEnumerable<string> availableModels)
    {
        if (!availableModels.Contains(modelId))
            throw new AiProviderException(AiFailure.ModelUnavailable);
        return selectedModel != modelId;
    }
}
