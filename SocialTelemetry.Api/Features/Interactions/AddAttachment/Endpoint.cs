using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.AddAttachment;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Options;

namespace SocialTelemetry.Api.Features.Interactions.AddAttachment;

public sealed class Endpoint(Scenario scenario, IOptions<UploadOptions> options) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/interactions/{interactionId}/attachments");
        AllowAnonymous();
        AllowFileUploads();
        MaxRequestBodySize(options.Value.AttachmentRequestBodyBytes);
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var file = request.File is null ? null : new UploadFile(request.File.Length, request.File.FileName,
                request.File.ContentType, request.File.OpenReadStream);
            var attachment = await scenario.ExecuteAsync(new Input(request.InteractionId, request.Type, request.TextContent, file), cancellationToken);
            await Send.ResponseAsync(new Response(attachment.Id, attachment.InteractionId, attachment.Type, attachment.Status,
                attachment.TextContent, attachment.StorageKey, attachment.MimeType, attachment.CreatedAt), 201, cancellationToken);
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
