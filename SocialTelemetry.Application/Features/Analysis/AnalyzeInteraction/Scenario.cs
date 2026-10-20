using System.Text.Json;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Observability;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using StoredSuggestion = SocialTelemetry.Domain.People.SuggestedProfileUpdate;

namespace SocialTelemetry.Application.Features.Analysis.AnalyzeInteraction;

public sealed class Scenario(IAnalysisPersistence persistence, AiContextBuilder contextBuilder, IAiClient aiClient,
    AnalysisOptions options, LocalProfileOperation operation)
{
    public async Task<Result> ExecuteAsync(Input input, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Lifetime);
        cancellationToken = operationCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        // The specific port commits/disposes its Serializable snapshot before inference.
        var built = await persistence.SnapshotAsync(
            token => contextBuilder.BuildAsync(input.InteractionId, input.UserQuestion, input.AttachmentIds, token), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = await aiClient.GetSelectedModelAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!selected.Supports(AiCapability.Text) || !selected.Supports(AiCapability.StructuredOutput) ||
            (built.Images.Count > 0 && !selected.Supports(AiCapability.Vision)))
            throw new AiProviderException(AiFailure.CapabilityMissing);
        AiTextResponse completed;
        using (var execution = SocialTelemetryTelemetry.StartAi(selected.ProviderId, selected.ModelId, built.Images.Count > 0))
        {
            try
            {
                completed = await aiClient.GenerateTextAsync(new AiTextRequest(AnalysisContract.Instructions, built.InputJson)
                {
                    Images = built.Images, StructuredOutput = AnalysisContract.OutputContract(), SelectedModel = selected
                }, cancellationToken);
                execution.SetExecutionProvenance(completed.ProviderId, completed.Model);
                execution.Complete();
            }
            catch (Exception exception)
            {
                execution.Fail(exception);
                throw;
            }
        }
        InteractionAnalysisResult result;
        if (completed.Binding is { } binding && binding.UserProfileId != built.Context.User.Id)
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        using (var validation = SocialTelemetryTelemetry.Start("analysis.validate_result", cancellationToken))
        {
            result = AnalysisContract.Validate(completed.Text, built.Context, options.ResultCharacters);
            validation.Complete();
        }
        await aiClient.ValidateExecutionAsync(completed, cancellationToken);
        using var persistenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, completed.LifetimeCancellationToken);
        InteractionAnalysis persisted;
        try
        {
            using var persistenceOperation = SocialTelemetryTelemetry.Start("analysis.persist", persistenceCancellation.Token);
            persisted = await PersistAsync(owner, input, built, completed, result, persistenceCancellation.Token);
            persistenceOperation.Complete();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && completed.LifetimeCancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        }
        return new Result(persisted.Id, persisted.InteractionId, result, completed.ProviderId, completed.Model,
            completed.RequestedModel, completed.ReturnedModel, AnalysisContract.SchemaVersion, AnalysisContract.PromptVersion, persisted.CreatedAt);
    }

    private Task<InteractionAnalysis> PersistAsync(Guid owner, Input input, BuiltAnalysisContext original,
        AiTextResponse completed, InteractionAnalysisResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(completed.ProviderId) || completed.ProviderId.Length > 200 ||
            string.IsNullOrWhiteSpace(completed.Model) || completed.Model.Length > 200)
            throw new AiProviderException(AiFailure.InvalidStructuredResponse);
        return persistence.CommitAsync(owner,
            token => VerifyContextAsync(input, original, token),
            token => aiClient.AcquireExecutionLeaseAsync(completed, token),
            () => CreateMutation(input, original, completed, result),
            cancellationToken);
    }

    private async Task VerifyContextAsync(Input input, BuiltAnalysisContext original, CancellationToken cancellationToken)
    {
        BuiltAnalysisContext current;
        try
        {
            current = await contextBuilder.BuildAsync(input.InteractionId, input.UserQuestion, input.AttachmentIds, cancellationToken);
        }
        catch (Exception exception) when (exception is NotFoundException or AiProviderException)
        {
            throw new AiProviderException(AiFailure.StaleContext);
        }
        if (current.Fingerprint != original.Fingerprint) throw new AiProviderException(AiFailure.StaleContext);
    }

    private static AnalysisMutation CreateMutation(Input input, BuiltAnalysisContext original,
        AiTextResponse completed, InteractionAnalysisResult result)
    {
        var analysis = new InteractionAnalysis
        {
            Id = Guid.NewGuid(), InteractionId = input.InteractionId, Summary = result.Summary,
            Provider = completed.ProviderId, Model = completed.Model, SchemaVersion = AnalysisContract.SchemaVersion,
            PromptVersion = AnalysisContract.PromptVersion, ContextFingerprint = original.Fingerprint,
            ResultJson = JsonSerializer.Serialize(result, AiContextBuilder.JsonOptions), CreatedAt = DateTimeOffset.UtcNow
        };
        var suggestions = new List<StoredSuggestion>();
        foreach (var suggestion in result.SuggestedProfileUpdates)
            suggestions.Add(new StoredSuggestion
            {
                Id = Guid.NewGuid(), PersonId = suggestion.PersonId, InteractionAnalysisId = analysis.Id,
                Field = suggestion.Field, SuggestedValue = suggestion.SuggestedValue, Status = SuggestionStatus.Pending,
                CreatedAt = analysis.CreatedAt
            });
        return new AnalysisMutation(analysis, suggestions);
    }
}
