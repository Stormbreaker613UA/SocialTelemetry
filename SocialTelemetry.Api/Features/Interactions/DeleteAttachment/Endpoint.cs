using SocialTelemetry.Api.Common.Profiles;
using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions.DeleteAttachment;

namespace SocialTelemetry.Api.Features.Interactions.DeleteAttachment;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/interactions/{interactionId}/attachments/{attachmentId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var attachmentId = await scenario.ExecuteAsync(request.InteractionId, request.AttachmentId, cancellationToken);
            var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
            await Send.OkAsync(new Response(attachmentId), transport);
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
