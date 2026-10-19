using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.DownloadAttachment;

namespace SocialTelemetry.Api.Features.Interactions.DownloadAttachment;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request>
{
    public override void Configure()
    {
        Get("/interactions/{interactionId}/attachments/{attachmentId}/content");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var attachment = await scenario.ExecuteAsync(request.InteractionId, request.AttachmentId, cancellationToken);
            await using var content = attachment.Content;
            HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
            await Send.StreamAsync(content, fileName: attachment.FileName, fileLengthBytes: content.Length,
                contentType: attachment.MimeType, cancellation: cancellationToken);
        }
        catch (FeatureValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(400, cancellationToken);
        }
        catch (NotFoundException)
        {
            await Send.NotFoundAsync(cancellationToken);
        }
    }
}
