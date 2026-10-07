using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Features.AiConnection;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.AI.Models;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Observability;
using StoredSuggestion = SocialTelemetry.Api.Domain.People.SuggestedProfileUpdate;

namespace SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;

public sealed class Endpoint(AppDbContext dbContext, AiContextBuilder contextBuilder, IAiClient aiClient,
    ILogger<Endpoint> logger, IOptions<AnalysisOptions> options) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/interactions/{interactionId}/analyze");
        AllowAnonymous();
        Options(route => route.WithMetadata(new RequireLocalAiRequestAttribute()));
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        using var operation = SocialTelemetryTelemetry.Start("analysis.analyze_interaction", cancellationToken);
        try
        {
            await AnalyzeAsync(request, cancellationToken);
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    private async Task AnalyzeAsync(Request request, CancellationToken cancellationToken)
    {
        BuiltAnalysisContext built;
        // A short snapshot protects context construction; no transaction spans provider inference.
        await using (var snapshot = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            using var contextOperation = SocialTelemetryTelemetry.Start("analysis.build_context", cancellationToken);
            built = await contextBuilder.BuildAsync(request.InteractionId, request.UserQuestion, request.AttachmentIds, cancellationToken);
            await snapshot.CommitAsync(cancellationToken);
            contextOperation.Complete();
        }
        var selected = await aiClient.GetSelectedModelAsync(cancellationToken);
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
        using (var validation = SocialTelemetryTelemetry.Start("analysis.validate_result", cancellationToken))
        {
            result = AnalysisContract.Validate(completed.Text, built.Context, options.Value.ResultCharacters);
            validation.Complete();
        }
        await aiClient.ValidateExecutionAsync(completed, cancellationToken);
        using var persistence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, completed.LifetimeCancellationToken);
        InteractionAnalysis persisted;
        try
        {
            using var persistenceOperation = SocialTelemetryTelemetry.Start("analysis.persist", persistence.Token);
            persisted = await PersistAsync(request, built, completed, result, persistence.Token);
            persistenceOperation.Complete();
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
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            // Acquire before reading: committed source changes are visible after any wait for a writer.
            await dbContext.LockAnalysisContextAsync(original.Context.User.Id, cancellationToken);
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
            // Acquire after the context check so no credential lock spans inference or waiting
            // for the database guard. Retain the lease through save and commit.
            using var executionLease = await aiClient.AcquireExecutionLeaseAsync(completed, cancellationToken);
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
            await executionLease.ValidateAsync(cancellationToken);
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
