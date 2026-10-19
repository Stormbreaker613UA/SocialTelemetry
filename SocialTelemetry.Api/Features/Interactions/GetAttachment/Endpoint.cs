using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.GetAttachment;

namespace SocialTelemetry.Api.Features.Interactions.GetAttachment;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/interactions/{interactionId}/attachments/{attachmentId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var attachment = await scenario.ExecuteAsync(request.InteractionId, request.AttachmentId, cancellationToken);
            await Send.OkAsync(new Response(attachment.Id, attachment.InteractionId, attachment.Type, attachment.Status,
                attachment.MimeType, attachment.TextContent, attachment.CreatedAt), cancellationToken);
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
