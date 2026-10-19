using SocialTelemetry.Application.Observability;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Contracts.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Application.Contracts.AI.Models;

namespace SocialTelemetry.Application.Features.Analysis;

public sealed class AiContextBuilder(IAnalysisContextQuery query, IAttachmentStorage storage, AnalysisOptions options,
    TranscriptOptions transcriptOptions, UploadOptions uploadOptions, LocalProfileOperation operation)
{
    private readonly AnalysisOptions limits = options;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BuiltAnalysisContext> BuildAsync(Guid interactionId, string? userQuestion,
        IReadOnlyList<Guid>? selectedAttachmentIds, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        CheckText(userQuestion, limits.QuestionCharacters);
        var interaction = CheckRead(await query.InteractionAsync(owner, interactionId, limits, cancellationToken))
            ?? throw new NotFoundException("Interaction not found.");
        var user = RequireRead(await query.UserAsync(interaction.UserProfileId, limits, cancellationToken));
        var participantIds = await query.ParticipantIdsAsync(owner, interactionId, limits.Participants + 1, cancellationToken);
        if (participantIds.Count == 0 || participantIds.Count > limits.Participants)
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var people = RequireRead(await query.PeopleAsync(owner, participantIds, limits, cancellationToken));
        if (people.Count != participantIds.Count) throw new AiProviderException(AiFailure.StaleContext);
        var history = RequireRead(await query.HistoryAsync(owner, interaction, participantIds, limits, cancellationToken));
        var sourceIds = history.Select(previous => previous.Id).Append(interactionId).ToArray();
        var participants = await LoadParticipantsAsync(interaction.UserProfileId, people, sourceIds, cancellationToken);
        foreach (var field in new[] { user.DisplayName, user.AboutMe, user.CommunicationStyle, user.Goals,
            user.Preferences, user.Boundaries, user.AiInstructions }) CheckText(field, limits.ProfileFieldCharacters);
        CheckText(interaction.Title, limits.InteractionTitleCharacters);
        CheckText(interaction.Description, limits.InteractionDescriptionCharacters);
        CheckText(interaction.UserThoughts, limits.UserThoughtsCharacters);
        var (evidence, images) = await LoadEvidenceAsync(owner, interactionId, selectedAttachmentIds, cancellationToken);
        var context = new InteractionAnalysisContext(new AnalysisUser(user.Id, user.DisplayName, user.AboutMe,
            user.CommunicationStyle, user.Goals, user.Preferences, user.Boundaries, user.AiInstructions),
            new AnalysisInteraction(interaction.Id, interaction.Title, interaction.Description, interaction.UserThoughts,
                interaction.OccurredAt, participantIds), participants, history, evidence, userQuestion);
        var json = JsonSerializer.Serialize(context, JsonOptions);
        CheckText(json, limits.ContextCharacters);
        return new BuiltAnalysisContext(context, images, json, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    private async Task<IReadOnlyList<AnalysisPerson>> LoadParticipantsAsync(Guid userProfileId,
        IReadOnlyList<SocialTelemetry.Domain.People.Person> people, Guid[] sourceIds, CancellationToken cancellationToken)
    {
        var participants = new List<AnalysisPerson>();
        foreach (var person in people)
        {
            var facts = RequireRead(await query.FactsAsync(userProfileId, person.Id, limits, cancellationToken));
            var inferences = RequireRead(await query.InferencesAsync(userProfileId, person.Id, sourceIds, limits, cancellationToken));
            foreach (var field in new[] { person.DisplayName, person.Gender, person.Description, person.HowWeMet, person.Notes }) CheckText(field, limits.ProfileFieldCharacters);
            participants.Add(new AnalysisPerson(person.Id, person.DisplayName, person.Age, person.Gender, person.Description,
                person.RelationshipContext, person.HowWeMet, person.Notes, facts, inferences));
        }
        return participants;
    }

    private async Task<(IReadOnlyList<AnalysisEvidence> Evidence, IReadOnlyList<AiImageInput> Images)> LoadEvidenceAsync(
        Guid owner, Guid interactionId, IReadOnlyList<Guid>? selectedIds, CancellationToken cancellationToken)
    {
        using var operation = SocialTelemetryTelemetry.Start("analysis.load_evidence", cancellationToken);
        if (selectedIds is not null && (selectedIds.Count > limits.EvidenceCount || selectedIds.Distinct().Count() != selectedIds.Count))
            throw new AiProviderException(AiFailure.InvalidEvidenceSelection);
        var attachments = RequireRead(await query.AttachmentsAsync(owner, interactionId, selectedIds, limits, cancellationToken));
        if (attachments.Count > limits.EvidenceCount) throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        if (selectedIds is not null && attachments.Count != selectedIds.Count) throw new AiProviderException(AiFailure.InvalidEvidenceSelection);
        var evidence = new List<AnalysisEvidence>();
        var images = new List<AiImageInput>();
        var totalBytes = 0;
        foreach (var attachment in attachments)
        {
            if (attachment.Status != AttachmentStatus.Ready) throw new AiProviderException(AiFailure.EvidenceNotReady);
            if (attachment.Type == AttachmentType.Text)
            {
                if (string.IsNullOrWhiteSpace(attachment.TextContent)) throw new AiProviderException(AiFailure.UnsupportedEvidence);
                evidence.Add(new AnalysisEvidence(attachment.Id, attachment.Type, [new EvidenceMessage(attachment.TextContent)], null, null));
                continue;
            }
            if (attachment.Type == AttachmentType.Audio)
            {
                evidence.Add(await LoadTranscriptEvidenceAsync(owner, attachment, cancellationToken));
                continue;
            }
            if (attachment.Type is not (AttachmentType.Image or AttachmentType.Screenshot))
                throw new AiProviderException(AiFailure.UnsupportedEvidence);
            if (images.Count >= limits.ImageCount) throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
            var mimeType = attachment.MimeType?.ToLowerInvariant();
            if (mimeType is not ("image/png" or "image/jpeg" or "image/webp")) throw new AiProviderException(AiFailure.UnsupportedEvidence);
            if (attachment.StorageKey is null) throw new AiProviderException(AiFailure.UnsupportedEvidence);
            await using var source = await storage.OpenReadAsync(attachment.StorageKey, cancellationToken)
                ?? throw new NotFoundException("Attachment content not found.");
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
            {
                if (buffer.Length + count > limits.ImageBytes || totalBytes + count > limits.TotalImageBytes)
                    throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
                buffer.Write(chunk, 0, count);
                totalBytes += count;
            }
            var bytes = buffer.ToArray();
            if (!HasImageSignature(bytes, mimeType)) throw new AiProviderException(AiFailure.UnsupportedEvidence);
            images.Add(new AiImageInput(attachment.Id, mimeType, bytes));
            evidence.Add(new AnalysisEvidence(attachment.Id, attachment.Type, [], mimeType, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        operation.Complete();
        return (evidence, images);
    }

    private async Task<AnalysisEvidence> LoadTranscriptEvidenceAsync(Guid owner, InteractionAttachment attachment, CancellationToken cancellationToken)
    {
        var textLimit = Math.Min(limits.TextEvidenceCharacters, transcriptOptions.TextCharacters);
        var transcript = CheckRead(await query.TranscriptAsync(owner, attachment.Id, attachment.InteractionId, textLimit, cancellationToken));
        if (transcript is null || transcript.ReviewStatus != TranscriptReviewStatus.Reviewed ||
            transcript.ReviewedAt is null || transcript.Version == Guid.Empty || string.IsNullOrWhiteSpace(transcript.Text))
            throw new AiProviderException(AiFailure.TranscriptNotReady);
        if (attachment.StorageKey is null || transcript.SourceStorageKey != attachment.StorageKey)
            throw new AiProviderException(AiFailure.StaleTranscript);
        CheckText(transcript.Text, textLimit);
        var digest = await ReadAudioDigestAsync(attachment.StorageKey, cancellationToken);
        if (transcript.SourceSha256 != digest) throw new AiProviderException(AiFailure.StaleTranscript);
        // Audio bytes only establish freshness. Analysis receives normalized untrusted text and provenance.
        return new AnalysisEvidence(attachment.Id, AttachmentType.Audio, [new EvidenceMessage(transcript.Text)], null, null,
            new AnalysisTranscript(transcript.Version, transcript.ReviewStatus, transcript.ReviewedAt.Value,
                transcript.SourceStorageKey, digest, transcript.Provider, transcript.Model, transcript.TranscriptionVersion));
    }

    private async Task<string> ReadAudioDigestAsync(string storageKey, CancellationToken cancellationToken)
    {
        await using var source = await storage.OpenReadAsync(storageKey, cancellationToken)
            ?? throw new NotFoundException("Attachment content not found.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            total += count;
            if (total > uploadOptions.AttachmentMaxBytes) throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
            hash.AppendData(buffer, 0, count);
        }
        if (total == 0) throw new AiProviderException(AiFailure.StaleTranscript);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static T? CheckRead<T>(BoundedContextRead<T> read) where T : class
    {
        if (read.ExceedsLimit) throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        return read.Value;
    }

    private static T RequireRead<T>(BoundedContextRead<T> read) where T : class =>
        CheckRead(read) ?? throw new InvalidOperationException("A bounded context read returned no value.");

    private static bool HasImageSignature(byte[] bytes, string mimeType) => mimeType switch
    {
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    private static void CheckText(string? text, int limit)
    {
        if (text?.Length > limit) throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
    }
}
