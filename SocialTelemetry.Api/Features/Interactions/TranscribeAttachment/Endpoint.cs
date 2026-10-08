using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Features.AiConnection;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Api.Infrastructure.Transcription;

namespace SocialTelemetry.Api.Features.Interactions.TranscribeAttachment;

public sealed class Endpoint(AppDbContext database, IAttachmentStorage storage, ISpeechToTextClient speech,
    IOptions<UploadOptions> uploads, IOptions<TranscriptOptions> transcripts) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/interactions/{interactionId}/attachments/{attachmentId}/transcribe");
        AllowAnonymous();
        Options(route => route.WithMetadata(new RequireLocalAiRequestAttribute()));
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var source = await LoadSourceAsync(request, cancellationToken);
        if (source.Type != AttachmentType.Audio) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
        if (source.Status != AttachmentStatus.Ready) throw new ConflictException();
        if (source.StorageKey is null) throw new NotFoundException();
        var audio = await ReadAudioAsync(source.StorageKey, cancellationToken);
        var digest = Convert.ToHexString(SHA256.HashData(audio));
        var initial = await database.AttachmentTranscripts.AsNoTracking().SingleOrDefaultAsync(
            transcript => transcript.AttachmentId == request.AttachmentId, cancellationToken);
        if (initial is not null && (initial.SourceStorageKey != source.StorageKey || initial.SourceSha256 != digest))
            throw new ConflictException();
        var effective = initial?.CorrectedText ?? initial?.GeneratedText;
        var reusable = !string.IsNullOrWhiteSpace(effective) && effective.Length <= transcripts.Value.TextCharacters;
        // No force-overwrite operation: manual/corrected/reviewed transcripts are never replaced by STT.
        if (!reusable && initial is not null && (initial.CorrectedText is not null || initial.ReviewStatus == TranscriptReviewStatus.Reviewed))
            throw new ConflictException();
        SpeechToTextResult? result = null;
        if (!reusable)
        {
            using var content = new MemoryStream(audio, writable: false);
            result = await speech.TranscribeAsync(content, cancellationToken);
            ValidateResult(result);
        }
        // Re-read through storage after execution. Database protection never spans native processing.
        if (Convert.ToHexString(SHA256.HashData(await ReadAudioAsync(source.StorageKey, cancellationToken))) != digest)
            throw new ConflictException();

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.LockAttachmentAnalysisContextAsync(request.AttachmentId, request.InteractionId, cancellationToken);
            var currentSource = await LoadSourceAsync(request, cancellationToken);
            if (currentSource != source) throw new ConflictException();
            var transcript = await database.AttachmentTranscripts.SingleOrDefaultAsync(
                transcript => transcript.AttachmentId == request.AttachmentId, cancellationToken);
            if (transcript?.Version != initial?.Version) throw new ConflictException();
            if (reusable)
            {
                if (transcript is null) throw new ConflictException();
                await transaction.CommitAsync(cancellationToken);
                await Send.OkAsync(Map(transcript, reused: true), cancellationToken);
                return;
            }
            if (result is null) throw new SpeechToTextException(SpeechToTextFailure.InvalidResult);
            var created = transcript is null;
            if (transcript is null)
            {
                transcript = new AttachmentTranscript { AttachmentId = request.AttachmentId, CreatedAt = DateTimeOffset.UtcNow };
                database.AttachmentTranscripts.Add(transcript);
            }
            // The version check covers newer edits; retain corrected text even when refreshing an empty generated record.
            transcript.GeneratedText = result.Text;
            transcript.Provider = result.ProviderId;
            transcript.Model = result.ModelId;
            transcript.TranscriptionVersion = result.TranscriptionVersion;
            transcript.SourceStorageKey = source.StorageKey;
            transcript.SourceSha256 = digest;
            transcript.ReviewStatus = TranscriptReviewStatus.Unreviewed;
            transcript.ReviewedAt = null;
            transcript.Version = Guid.NewGuid();
            transcript.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await Send.ResponseAsync(Map(transcript, reused: false), created ? 201 : 200, cancellationToken);
        }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(); }
        catch (Exception exception) when (AnalysisPersistence.IsConcurrentSourceChange(exception)) { throw new ConflictException(); }
    }

    private async Task<SourceState> LoadSourceAsync(Request request, CancellationToken cancellationToken) =>
        await database.InteractionAttachments.AsNoTracking()
            .Where(attachment => attachment.Id == request.AttachmentId && attachment.InteractionId == request.InteractionId)
            .Select(attachment => new SourceState(attachment.Interaction.UserProfileId, attachment.Type, attachment.Status, attachment.StorageKey))
            .SingleOrDefaultAsync(cancellationToken) ?? throw new NotFoundException();

    private async Task<byte[]> ReadAudioAsync(string key, CancellationToken cancellationToken)
    {
        await using var source = await storage.OpenReadAsync(key, cancellationToken) ?? throw new NotFoundException();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
        {
            if (buffer.Length + count > uploads.Value.AttachmentMaxBytes) throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
            buffer.Write(chunk, 0, count);
        }
        if (buffer.Length == 0) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
        return buffer.ToArray();
    }

    private void ValidateResult(SpeechToTextResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > transcripts.Value.TextCharacters ||
            new[] { result.ProviderId, result.ModelId, result.TranscriptionVersion }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200))
            throw new SpeechToTextException(SpeechToTextFailure.InvalidResult);
    }

    private static Response Map(AttachmentTranscript transcript, bool reused) => new(transcript.AttachmentId,
        transcript.GeneratedText, transcript.CorrectedText, transcript.CorrectedText ?? transcript.GeneratedText ?? string.Empty,
        transcript.ReviewStatus, transcript.ReviewedAt, transcript.Version, transcript.SourceSha256, transcript.Provider,
        transcript.Model, transcript.TranscriptionVersion, transcript.CreatedAt, transcript.UpdatedAt, reused);
    private sealed record SourceState(Guid ProfileId, AttachmentType Type, AttachmentStatus Status, string? StorageKey);
}
