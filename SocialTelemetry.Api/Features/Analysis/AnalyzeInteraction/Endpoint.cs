using FastEndpoints;
using SocialTelemetry.Application.Observability;
using SocialTelemetry.Api.Features.AiConnection;
using Scenario = SocialTelemetry.Application.Features.Analysis.AnalyzeInteraction.Scenario;
using Input = SocialTelemetry.Application.Features.Analysis.AnalyzeInteraction.Input;

namespace SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;

public sealed class Endpoint(Scenario scenario, ILogger<Endpoint> logger) : Endpoint<Request, Response>
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
            var result = await scenario.ExecuteAsync(new Input(request.InteractionId, request.UserQuestion, request.AttachmentIds), cancellationToken);
            logger.LogInformation("Analysis {AnalysisId} created for Interaction {InteractionId} with {SuggestionCount} pending suggestions",
                result.Id, result.InteractionId, result.Analysis.SuggestedProfileUpdates.Count);
            await Send.ResponseAsync(new Response(result.Id, result.InteractionId, result.Analysis, result.Provider,
                result.Model, result.RequestedModel, result.ReturnedModel, result.SchemaVersion, result.PromptVersion, result.CreatedAt),
                201, cancellationToken);
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }
}
