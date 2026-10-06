using System.Data;
using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Features.AiConnection;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.AI.Models;
using SocialTelemetry.Api.Infrastructure.Persistence;
using StoredSuggestion = SocialTelemetry.Api.Domain.People.SuggestedProfileUpdate;

namespace SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;

public sealed class Endpoint(AppDbContext dbContext, AiContextBuilder contextBuilder, IAiClient aiClient,
    ILogger<Endpoint> logger) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/interactions/{interactionId}/analyze");
        AllowAnonymous();
        Options(route => route.WithMetadata(new RequireLocalAiRequestAttribute()));
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        BuiltAnalysisContext built;
        // A short snapshot protects context construction; no transaction spans provider inference.
        await using (var snapshot = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            built = await contextBuilder.BuildAsync(request.InteractionId, request.UserQuestion, request.AttachmentIds, cancellationToken);
            await snapshot.CommitAsync(cancellationToken);
        }
        var selected = await aiClient.GetSelectedModelAsync(cancellationToken);
        if (!selected.Supports(AiCapability.Text) || !selected.Supports(AiCapability.StructuredOutput) ||
            (built.Images.Count > 0 && !selected.Supports(AiCapability.Vision)))
            throw new AiProviderException(AiFailure.CapabilityMissing);
        var completed = await aiClient.GenerateTextAsync(new AiTextRequest(AnalysisContract.Instructions, built.InputJson)
        {
            Images = built.Images, StructuredOutput = AnalysisContract.OutputContract(), SelectedModel = selected
        }, cancellationToken);
        var result = AnalysisContract.Validate(completed.Text, built.Context);
        await aiClient.ValidateExecutionAsync(completed, cancellationToken);
        using var persistence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, completed.LifetimeCancellationToken);
        InteractionAnalysis persisted;
        try
        {
            persisted = await PersistAsync(request, built, completed, result, persistence.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && completed.LifetimeCancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailure.ExecutionInvalidated);
        }
        logger.LogInformation("Analysis {AnalysisId} created for Interaction {InteractionId} with {SuggestionCount} pending suggestions",
            persisted.Id, persisted.InteractionId, result.SuggestedProfileUpdates.Count);
        await Send.ResponseAsync(new Response(persisted.Id, persisted.InteractionId, result, completed.ProviderId,
            completed.Model, completed.RequestedModel, completed.ReturnedModel, AnalysisContract.SchemaVersion,
            AnalysisContract.PromptVersion, persisted.CreatedAt), 201, cancellationToken);
    }

    private async Task<InteractionAnalysis> PersistAsync(Request request, BuiltAnalysisContext original,
        AiTextResponse completed, InteractionAnalysisResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(completed.ProviderId) || completed.ProviderId.Length > 200 ||
            string.IsNullOrWhiteSpace(completed.Model) || completed.Model.Length > 200)
            throw new AiProviderException(AiFailure.InvalidStructuredResponse);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Acquire before reading: committed source changes are visible after any wait for a writer.
            await dbContext.LockAnalysisContextAsync(cancellationToken);
            BuiltAnalysisContext current;
            try
            {
                current = await contextBuilder.BuildAsync(request.InteractionId, request.UserQuestion, request.AttachmentIds, cancellationToken);
            }
            catch (Exception exception) when (exception is NotFoundException or AiProviderException)
            {
                throw new AiProviderException(AiFailure.StaleContext);
            }
            if (current.Fingerprint != original.Fingerprint) throw new AiProviderException(AiFailure.StaleContext);
            await aiClient.ValidateExecutionAsync(completed, cancellationToken);
            var analysis = new InteractionAnalysis
            {
                Id = Guid.NewGuid(), InteractionId = request.InteractionId, Summary = result.Summary,
                Provider = completed.ProviderId, Model = completed.Model, SchemaVersion = AnalysisContract.SchemaVersion,
                PromptVersion = AnalysisContract.PromptVersion, ContextFingerprint = original.Fingerprint,
                ResultJson = JsonSerializer.Serialize(result, AiContextBuilder.JsonOptions), CreatedAt = DateTimeOffset.UtcNow
            };
            dbContext.InteractionAnalyses.Add(analysis);
            foreach (var suggestion in result.SuggestedProfileUpdates)
                dbContext.SuggestedProfileUpdates.Add(new StoredSuggestion
                {
                    Id = Guid.NewGuid(), PersonId = suggestion.PersonId, InteractionAnalysisId = analysis.Id,
                    Field = suggestion.Field, SuggestedValue = suggestion.SuggestedValue, Status = SuggestionStatus.Pending,
                    CreatedAt = analysis.CreatedAt
                });
            await dbContext.SaveChangesAsync(cancellationToken);
            await aiClient.ValidateExecutionAsync(completed, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return analysis;
        }
        catch (Exception exception) when (AnalysisPersistence.IsConcurrentSourceChange(exception))
        {
            // A concurrent source deletion/update can invalidate the short persistence transaction.
            throw new AiProviderException(AiFailure.StaleContext);
        }
    }
}
