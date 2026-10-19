using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.GetTranscript;

namespace SocialTelemetry.Api.Features.Interactions.GetTranscript;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/interactions/{interactionId}/attachments/{attachmentId}/transcript");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var transcript = await scenario.ExecuteAsync(request.InteractionId, request.AttachmentId, cancellationToken);
            var response = new Response(transcript.AttachmentId, transcript.GeneratedText, transcript.CorrectedText,
                transcript.CorrectedText ?? transcript.GeneratedText ?? string.Empty, transcript.ReviewStatus, transcript.ReviewedAt,
                transcript.Version, transcript.SourceSha256, transcript.Provider, transcript.Model, transcript.TranscriptionVersion,
                transcript.CreatedAt, transcript.UpdatedAt);
            await Send.OkAsync(response, cancellationToken);
        }
        catch (FeatureValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(400, cancellationToken);
        }
    }
}
