using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.Interactions.PutTranscript;

public sealed class Endpoint(AppDbContext database, IAttachmentStorage storage,
    IOptions<TranscriptOptions> options, IOptions<UploadOptions> uploadOptions) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/interactions/{interactionId}/attachments/{attachmentId}/transcript");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (request.CorrectedText is not null && (string.IsNullOrWhiteSpace(request.CorrectedText) ||
            request.CorrectedText.Length > options.Value.TextCharacters))
        {
            await BadRequestAsync("Transcript text must be non-empty and within the configured limit.", cancellationToken);
            return;
        }
        var attachment = await database.InteractionAttachments.AsNoTracking().SingleOrDefaultAsync(
            attachment => attachment.Id == request.AttachmentId && attachment.InteractionId == request.InteractionId, cancellationToken)
            ?? throw new NotFoundException();
        if (attachment.Type != AttachmentType.Audio)
        {
            await BadRequestAsync("Transcripts require an Audio attachment.", cancellationToken);
            return;
        }
        if (attachment.Status != AttachmentStatus.Ready) throw new ConflictException();
        if (attachment.StorageKey is null) throw new NotFoundException();
        // Files are immutable through the attachment API. Read before acquiring database protection.
        var digest = await ReadDigestAsync(attachment.StorageKey, cancellationToken);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.LockAttachmentAnalysisContextAsync(attachment.Id, request.InteractionId, cancellationToken);
            var currentAttachment = await database.InteractionAttachments.AsNoTracking()
                .SingleOrDefaultAsync(current => current.Id == attachment.Id && current.InteractionId == request.InteractionId, cancellationToken)
                ?? throw new NotFoundException();
            if (currentAttachment.Status != AttachmentStatus.Ready || currentAttachment.Type != AttachmentType.Audio ||
                currentAttachment.StorageKey != attachment.StorageKey) throw new ConflictException();

            var transcript = await database.AttachmentTranscripts.SingleOrDefaultAsync(
                transcript => transcript.AttachmentId == attachment.Id, cancellationToken);
            var created = transcript is null;
            if (transcript is null)
            {
                if (request.ExpectedVersion is not null) throw new ConflictException();
                if (request.CorrectedText is null)
                {
                    await BadRequestAsync("Manual creation requires transcript text.", cancellationToken);
                    return;
                }
                transcript = new AttachmentTranscript
                {
                    AttachmentId = attachment.Id, SourceStorageKey = attachment.StorageKey, SourceSha256 = digest,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                database.AttachmentTranscripts.Add(transcript);
            }
            else if (request.ExpectedVersion != transcript.Version || transcript.SourceStorageKey != attachment.StorageKey ||
                transcript.SourceSha256 != digest) throw new ConflictException();

            var correctedText = request.CorrectedText ?? transcript.CorrectedText;
            var changed = created || correctedText != transcript.CorrectedText;
            var effectiveText = correctedText ?? transcript.GeneratedText;
            if (string.IsNullOrWhiteSpace(effectiveText) || effectiveText.Length > options.Value.TextCharacters)
            {
                await BadRequestAsync("Transcript text must be non-empty and within the configured limit.", cancellationToken);
                return;
            }
            if (changed && request.ConfirmReviewed)
            {
                await BadRequestAsync("Save changed text first, then confirm the returned version as reviewed.", cancellationToken);
                return;
            }
            transcript.CorrectedText = correctedText;
            if (changed)
            {
                transcript.ReviewStatus = TranscriptReviewStatus.Unreviewed;
                transcript.ReviewedAt = null;
            }
            else if (request.ConfirmReviewed)
            {
                transcript.ReviewStatus = TranscriptReviewStatus.Reviewed;
                transcript.ReviewedAt = DateTimeOffset.UtcNow;
            }
            transcript.Version = Guid.NewGuid();
            transcript.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await Send.ResponseAsync(new Response(transcript.AttachmentId, transcript.GeneratedText, transcript.CorrectedText,
                effectiveText, transcript.ReviewStatus, transcript.ReviewedAt, transcript.Version, transcript.SourceSha256,
                transcript.Provider, transcript.Model, transcript.TranscriptionVersion, transcript.CreatedAt, transcript.UpdatedAt),
                created ? 201 : 200, cancellationToken);
        }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(); }
        catch (Exception exception) when (AnalysisPersistence.IsConcurrentSourceChange(exception)) { throw new ConflictException(); }
    }

    private async Task<string> ReadDigestAsync(string storageKey, CancellationToken cancellationToken)
    {
        await using var source = await storage.OpenReadAsync(storageKey, cancellationToken) ?? throw new NotFoundException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            total += count;
            if (total > uploadOptions.Value.AttachmentMaxBytes) throw new ConflictException();
            hash.AppendData(buffer, 0, count);
        }
        if (total == 0) throw new ConflictException();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task BadRequestAsync(string message, CancellationToken cancellationToken)
    {
        AddError(message);
        await Send.ErrorsAsync(400, cancellationToken);
    }
}
