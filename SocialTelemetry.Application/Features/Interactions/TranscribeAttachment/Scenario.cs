using System.Security.Cryptography;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.TranscribeAttachment;

public sealed class Scenario(ITranscriptPersistence persistence, LocalProfileOperation operation, IAttachmentStorage storage,
    ISpeechToTextClient speech, UploadOptions uploads, TranscriptOptions transcripts)
{
    public async Task<TranscriptMutation> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Lifetime);
        cancellationToken = lifetime.Token;
        var source = await persistence.LoadSourceAsync(ownerId, request.InteractionId, request.AttachmentId, cancellationToken);
        if (source.Type != AttachmentType.Audio) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
        if (source.Status != AttachmentStatus.Ready) throw new ConflictException();
        if (source.StorageKey is null) throw new NotFoundException();
        var audio = await ReadAudioAsync(source.StorageKey, cancellationToken);
        var digest = Convert.ToHexString(SHA256.HashData(audio));
        var initial = await persistence.ReadTranscriptAsync(ownerId, request.AttachmentId, cancellationToken);
        if (initial is not null && (initial.SourceStorageKey != source.StorageKey || initial.SourceSha256 != digest))
            throw new ConflictException();
        var effective = initial?.CorrectedText ?? initial?.GeneratedText;
        var reusable = !string.IsNullOrWhiteSpace(effective) && effective.Length <= transcripts.TextCharacters;
        // No force-overwrite operation: manual/corrected/reviewed transcripts are never replaced by STT.
        if (!reusable && initial is not null && (initial.CorrectedText is not null || initial.ReviewStatus == TranscriptReviewStatus.Reviewed))
            throw new ConflictException();
        SpeechToTextResult? result = null;
        if (!reusable)
        {
            await operation.RecordStartAsync(cancellationToken);
            using var content = new MemoryStream(audio, writable: false);
            result = await speech.TranscribeAsync(content, cancellationToken);
            ValidateResult(result);
        }
        // Re-read through storage after execution. Database protection never spans native processing.
        if (Convert.ToHexString(SHA256.HashData(await ReadAudioAsync(source.StorageKey, cancellationToken))) != digest)
            throw new ConflictException();

        return await persistence.CommitTranscriptAsync(ownerId, request.InteractionId, request.AttachmentId, ValidateSource, Decide, cancellationToken);

        void ValidateSource(TranscriptSource currentSource)
        {
            if (currentSource != source) throw new ConflictException();
        }

        TranscriptMutation Decide(AttachmentTranscript? transcript)
        {
            if (transcript?.Version != initial?.Version) throw new ConflictException();
            if (reusable)
            {
                if (transcript is null) throw new ConflictException();
                return new TranscriptMutation(transcript, SaveChanges: false, Created: false, Reused: true);
            }
            if (result is null) throw new SpeechToTextException(SpeechToTextFailure.InvalidResult);
            var created = transcript is null;
            if (transcript is null)
            {
                transcript = new AttachmentTranscript { AttachmentId = request.AttachmentId, CreatedAt = DateTimeOffset.UtcNow };
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

            return new TranscriptMutation(transcript, SaveChanges: true, Created: created, Reused: false);
        }
    }

    private async Task<byte[]> ReadAudioAsync(string key, CancellationToken cancellationToken)
    {
        await using var source = await storage.OpenReadAsync(key, cancellationToken) ?? throw new NotFoundException();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
        {
            if (buffer.Length + count > uploads.AttachmentMaxBytes) throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
            buffer.Write(chunk, 0, count);
        }
        if (buffer.Length == 0) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
        return buffer.ToArray();
    }

    private void ValidateResult(SpeechToTextResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > transcripts.TextCharacters ||
            new[] { result.ProviderId, result.ModelId, result.TranscriptionVersion }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200))
            throw new SpeechToTextException(SpeechToTextFailure.InvalidResult);
    }

}
