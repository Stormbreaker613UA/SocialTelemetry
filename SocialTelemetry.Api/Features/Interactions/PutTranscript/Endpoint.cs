using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.PutTranscript;

namespace SocialTelemetry.Api.Features.Interactions.PutTranscript;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/interactions/{interactionId}/attachments/{attachmentId}/transcript");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await scenario.ExecuteAsync(new Input
            {
                InteractionId = request.InteractionId,
                AttachmentId = request.AttachmentId,
                ExpectedVersion = request.ExpectedVersion,
                CorrectedText = request.CorrectedText,
                ConfirmReviewed = request.ConfirmReviewed
            }, cancellationToken);
            var transcript = result.Transcript;
            var response = new Response(transcript.AttachmentId, transcript.GeneratedText, transcript.CorrectedText,
                transcript.CorrectedText ?? transcript.GeneratedText ?? string.Empty, transcript.ReviewStatus, transcript.ReviewedAt,
                transcript.Version, transcript.SourceSha256, transcript.Provider, transcript.Model, transcript.TranscriptionVersion,
                transcript.CreatedAt, transcript.UpdatedAt);
            await Send.ResponseAsync(response, result.Created ? 201 : 200, cancellationToken);
        }
        catch (FeatureValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(400, cancellationToken);
        }
    }
}
