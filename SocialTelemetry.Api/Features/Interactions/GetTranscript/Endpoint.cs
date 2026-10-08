using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.Interactions.GetTranscript;

public sealed class Endpoint(AppDbContext database) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/interactions/{interactionId}/attachments/{attachmentId}/transcript");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var attachment = await database.InteractionAttachments.AsNoTracking()
            .Where(attachment => attachment.Id == request.AttachmentId && attachment.InteractionId == request.InteractionId)
            .Select(attachment => new { attachment.Type, attachment.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (attachment is null) throw new NotFoundException();
        if (attachment.Type != AttachmentType.Audio)
        {
            AddError("Transcripts require an Audio attachment.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }
        if (attachment.Status != AttachmentStatus.Ready) throw new ConflictException();
        var response = await database.AttachmentTranscripts.AsNoTracking()
            .Where(transcript => transcript.AttachmentId == request.AttachmentId &&
                transcript.Attachment.InteractionId == request.InteractionId &&
                transcript.Attachment.Type == AttachmentType.Audio && transcript.Attachment.Status == AttachmentStatus.Ready)
            .Select(transcript => new Response(transcript.AttachmentId, transcript.GeneratedText, transcript.CorrectedText,
                transcript.CorrectedText ?? transcript.GeneratedText ?? string.Empty, transcript.ReviewStatus, transcript.ReviewedAt,
                transcript.Version, transcript.SourceSha256, transcript.Provider, transcript.Model, transcript.TranscriptionVersion,
                transcript.CreatedAt, transcript.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (response is null) throw new NotFoundException();
        await Send.OkAsync(response, cancellationToken);
    }
}
