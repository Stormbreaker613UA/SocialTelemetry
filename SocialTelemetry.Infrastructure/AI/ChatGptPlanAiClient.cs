using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.AI.Models;

namespace SocialTelemetry.Infrastructure.AI;

public sealed class ChatGptPlanAiClient(ChatGptConnection connection, ChatGptHttpClient provider, ProfileAiAccess profileAccess,
    ChatGptCapabilityResolver capabilityResolver) : IAiClient
{
    private const string ProviderId = "chatgpt-plan";

    public async Task<AiSelectedModel> GetSelectedModelAsync(CancellationToken cancellationToken)
    {
        var selection = await profileAccess.CaptureAsync(cancellationToken);
        if (selection.Provider != ProviderId) throw new AiProviderException(AiFailure.NotConnected);
        return new AiSelectedModel(ProviderId, selection.Model, capabilityResolver.Resolve(selection.Model)) { Binding = selection.Binding };
    }

    public async Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken)
    {
        var selection = await profileAccess.CaptureAsync(cancellationToken);
        var binding = selection.Binding;
        var selectionLifetime = selection.Lifetime;
        if (selection.Provider != ProviderId || (request.SelectedModel is not null && request.SelectedModel.Binding != binding))
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        var session = await connection.GetOwnedInferenceSessionAsync(binding.UserProfileId, binding.ConnectionId, selection.Model, cancellationToken);
        if (request.SelectedModel is { } selected &&
            (selected.ProviderId != ProviderId || selected.ModelId != session.Model))
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        var capabilities = capabilityResolver.Resolve(session.Model);
        if (!capabilities.Supports(AiCapability.Text) ||
            (request.Images.Count > 0 && !capabilities.Supports(AiCapability.Vision)) ||
            (request.StructuredOutput is not null && !capabilities.Supports(AiCapability.StructuredOutput)))
            throw new AiProviderException(AiFailure.CapabilityMissing);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.LifetimeCancellationToken, selectionLifetime);
        try
        {
            var result = await provider.GenerateAsync(session.AccessToken, session.Model, request, execution.Token);
            var completed = new AiTextResponse(result.Text, ProviderId, session.Model, result.ReturnedModel)
            {
                ExecutionId = session.SessionId,
                LifetimeCancellationToken = selectionLifetime,
                Binding = binding
            };
            await ValidateExecutionAsync(completed, cancellationToken);
            return completed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
            (session.LifetimeCancellationToken.IsCancellationRequested || selectionLifetime.IsCancellationRequested))
        {
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        }
    }

    public async Task ValidateExecutionAsync(AiTextResponse result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.LifetimeCancellationToken.IsCancellationRequested)
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        var binding = result.Binding ?? throw new AiProviderException(AiFailure.ExecutionInvalidated);
        await profileAccess.ValidateAsync(binding, cancellationToken);
        await connection.ValidateOwnedSessionAsync(binding.UserProfileId, binding.ConnectionId, result.ExecutionId, cancellationToken);
        if (result.LifetimeCancellationToken.IsCancellationRequested)
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
    }

    public async Task<IAiExecutionLease> AcquireExecutionLeaseAsync(AiTextResponse result, CancellationToken cancellationToken)
    {
        var binding = result.Binding ?? throw new AiProviderException(AiFailure.ExecutionInvalidated);
        await profileAccess.ProtectAsync(binding, cancellationToken);
        return await connection.AcquireOwnedLeaseAsync(binding.UserProfileId, binding.ConnectionId, result.ExecutionId, result.LifetimeCancellationToken, cancellationToken);
    }
}
