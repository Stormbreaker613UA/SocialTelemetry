using System.Security.Cryptography;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Application.Features.Interactions.PutTranscript;

public sealed class Scenario(ITranscriptPersistence persistence, LocalProfileOperation operation, IAttachmentStorage storage,
    TranscriptOptions options, UploadOptions uploadOptions)
{
    public async Task<TranscriptMutation> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        cancellationToken = admission.CancellationToken;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Lifetime);
        cancellationToken = lifetime.Token;
        if (request.CorrectedText is not null && (string.IsNullOrWhiteSpace(request.CorrectedText) ||
            request.CorrectedText.Length > options.TextCharacters))
            throw new FeatureValidationException("Transcript text must be non-empty and within the configured limit.");
        var attachment = await persistence.LoadSourceAsync(ownerId, request.InteractionId, request.AttachmentId, cancellationToken);
        if (attachment.Type != AttachmentType.Audio)
            throw new FeatureValidationException("Transcripts require an Audio attachment.");
        if (attachment.Status != AttachmentStatus.Ready) throw new ConflictException();
        var sourceKey = attachment.StorageKey ?? throw new NotFoundException();
        // Immutable file reads finish before the short guarded database operation.
        var digest = await ReadDigestAsync(sourceKey, cancellationToken);
        return await persistence.CommitTranscriptAsync(ownerId, request.InteractionId, request.AttachmentId, ValidateSource, Decide, cancellationToken);

        void ValidateSource(TranscriptSource currentAttachment)
        {
            if (currentAttachment.Status != AttachmentStatus.Ready || currentAttachment.Type != AttachmentType.Audio ||
                currentAttachment.StorageKey != sourceKey) throw new ConflictException();
        }

        TranscriptMutation Decide(AttachmentTranscript? transcript)
        {
            var created = transcript is null;
            if (transcript is null)
            {
                if (request.ExpectedVersion is not null) throw new ConflictException();
                if (request.CorrectedText is null)
                {
                    throw new FeatureValidationException("Manual creation requires transcript text.");
                }
                transcript = new AttachmentTranscript
                {
                    AttachmentId = request.AttachmentId, SourceStorageKey = sourceKey, SourceSha256 = digest,
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }
            else if (request.ExpectedVersion != transcript.Version || transcript.SourceStorageKey != sourceKey ||
                transcript.SourceSha256 != digest) throw new ConflictException();

            var correctedText = request.CorrectedText ?? transcript.CorrectedText;
            var changed = created || correctedText != transcript.CorrectedText;
            var effectiveText = correctedText ?? transcript.GeneratedText;
            if (string.IsNullOrWhiteSpace(effectiveText) || effectiveText.Length > options.TextCharacters)
            {
                throw new FeatureValidationException("Transcript text must be non-empty and within the configured limit.");
            }
            if (changed && request.ConfirmReviewed)
            {
                throw new FeatureValidationException("Save changed text first, then confirm the returned version as reviewed.");
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

            return new TranscriptMutation(transcript, SaveChanges: true, Created: created, Reused: false);
        }
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
            if (total > uploadOptions.AttachmentMaxBytes) throw new ConflictException();
            hash.AppendData(buffer, 0, count);
        }
        if (total == 0) throw new ConflictException();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

}
