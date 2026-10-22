using SocialTelemetry.Application.Contracts.AI;

namespace SocialTelemetry.Application.Features.AiConnection.SelectModel;

public sealed class Scenario(IProfileAiManagement connection, SocialTelemetry.Application.Contracts.Profiles.LocalProfileOperation operation)
{
    public async Task ExecuteAsync(string modelId, CancellationToken cancellationToken)
    {
        operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        ValidateInput(modelId);
        await connection.SelectModelAsync(modelId, cancellationToken);
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
