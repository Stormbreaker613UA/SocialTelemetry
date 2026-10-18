using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.AI.Models;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Api.Infrastructure.Observability;
using SocialTelemetry.Api.Infrastructure.Profiles;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class AiContextBuilder(AppDbContext dbContext, IAttachmentStorage storage, IOptions<AnalysisOptions> options,
    IOptions<TranscriptOptions> transcriptOptions, IOptions<UploadOptions> uploadOptions, LocalProfileOperation? operation = null)
{
    private readonly AnalysisOptions limits = options.Value;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BuiltAnalysisContext> BuildAsync(Guid interactionId, string? userQuestion,
        IReadOnlyList<Guid>? selectedAttachmentIds, CancellationToken cancellationToken)
    {
        CheckText(userQuestion, limits.QuestionCharacters);
        var interactionQuery = dbContext.Interactions.AsNoTracking().Where(stored => stored.Id == interactionId);
        if (operation?.Binding is { } binding)
            interactionQuery = interactionQuery.Where(stored => stored.UserProfileId == binding.UserProfileId);
        if (await interactionQuery.AnyAsync(stored => stored.Title.Length > limits.InteractionTitleCharacters || stored.Description.Length > limits.InteractionDescriptionCharacters ||
            (stored.UserThoughts != null && stored.UserThoughts.Length > limits.UserThoughtsCharacters), cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var interaction = await interactionQuery.SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Interaction not found.");
        var userQuery = dbContext.UserProfiles.AsNoTracking().Where(profile => profile.Id == interaction.UserProfileId);
        if (await userQuery.AnyAsync(profile => profile.DisplayName.Length > limits.ProfileFieldCharacters ||
            (profile.AboutMe != null && profile.AboutMe.Length > limits.ProfileFieldCharacters) ||
            (profile.CommunicationStyle != null && profile.CommunicationStyle.Length > limits.ProfileFieldCharacters) ||
            (profile.Goals != null && profile.Goals.Length > limits.ProfileFieldCharacters) ||
            (profile.Preferences != null && profile.Preferences.Length > limits.ProfileFieldCharacters) ||
            (profile.Boundaries != null && profile.Boundaries.Length > limits.ProfileFieldCharacters) ||
            (profile.AiInstructions != null && profile.AiInstructions.Length > limits.ProfileFieldCharacters), cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var user = await userQuery.SingleAsync(cancellationToken);
        var participantIds = await dbContext.InteractionParticipants.AsNoTracking()
            .Where(participant => participant.InteractionId == interactionId)
            .OrderBy(participant => participant.PersonId).Select(participant => participant.PersonId)
            .Take(limits.Participants + 1).ToListAsync(cancellationToken);
        if (participantIds.Count == 0 || participantIds.Count > limits.Participants)
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var peopleQuery = dbContext.People.AsNoTracking()
            .Where(person => participantIds.Contains(person.Id) && person.UserProfileId == interaction.UserProfileId);
        if (await peopleQuery.AnyAsync(person => person.DisplayName.Length > limits.ProfileFieldCharacters ||
            (person.Gender != null && person.Gender.Length > limits.ProfileFieldCharacters) ||
            (person.Description != null && person.Description.Length > limits.ProfileFieldCharacters) ||
            (person.HowWeMet != null && person.HowWeMet.Length > limits.ProfileFieldCharacters) ||
            (person.Notes != null && person.Notes.Length > limits.ProfileFieldCharacters), cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var people = await peopleQuery.OrderBy(person => person.Id).ToListAsync(cancellationToken);
        if (people.Count != participantIds.Count) throw new AiProviderException(AiFailure.StaleContext);

        // Bounded recent earlier interactions with an overlapping participant; exclude interactions
        // containing any foreign/non-current participant so unrelated People never enter context.
        var historyQuery = dbContext.Interactions.AsNoTracking().Where(previous =>
            previous.UserProfileId == interaction.UserProfileId && previous.Id != interactionId &&
            previous.OccurredAt <= interaction.OccurredAt &&
            previous.Participants.Any(participant => participantIds.Contains(participant.PersonId)) &&
            !previous.Participants.Any(participant => !participantIds.Contains(participant.PersonId) ||
                participant.Person.UserProfileId != interaction.UserProfileId))
            .OrderByDescending(previous => previous.OccurredAt).ThenBy(previous => previous.Id)
            .Take(limits.PreviousInteractions);
        if (await historyQuery.AnyAsync(previous => previous.Title.Length > limits.InteractionTitleCharacters ||
            previous.Description.Length > limits.InteractionDescriptionCharacters || (previous.UserThoughts != null && previous.UserThoughts.Length > limits.UserThoughtsCharacters), cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var history = await historyQuery.Select(previous => new AnalysisInteraction(previous.Id, previous.Title,
            previous.Description, previous.UserThoughts, previous.OccurredAt,
            previous.Participants.OrderBy(participant => participant.PersonId).Select(participant => participant.PersonId).ToList()))
            .ToListAsync(cancellationToken);
        var sourceIds = history.Select(previous => previous.Id).Append(interactionId).ToArray();
        var participants = await LoadParticipantsAsync(interaction.UserProfileId, people, sourceIds, cancellationToken);
        foreach (var field in new[] { user.DisplayName, user.AboutMe, user.CommunicationStyle, user.Goals,
            user.Preferences, user.Boundaries, user.AiInstructions }) CheckText(field, limits.ProfileFieldCharacters);
        CheckText(interaction.Title, limits.InteractionTitleCharacters);
        CheckText(interaction.Description, limits.InteractionDescriptionCharacters);
        CheckText(interaction.UserThoughts, limits.UserThoughtsCharacters);
        var (evidence, images) = await LoadEvidenceAsync(interactionId, selectedAttachmentIds, cancellationToken);
        var context = new InteractionAnalysisContext(new AnalysisUser(user.Id, user.DisplayName, user.AboutMe,
            user.CommunicationStyle, user.Goals, user.Preferences, user.Boundaries, user.AiInstructions),
            new AnalysisInteraction(interaction.Id, interaction.Title, interaction.Description, interaction.UserThoughts,
                interaction.OccurredAt, participantIds), participants, history, evidence, userQuestion);
        var json = JsonSerializer.Serialize(context, JsonOptions);
        CheckText(json, limits.ContextCharacters);
        return new BuiltAnalysisContext(context, images, json, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    private async Task<IReadOnlyList<AnalysisPerson>> LoadParticipantsAsync(Guid userProfileId,
        IReadOnlyList<SocialTelemetry.Api.Domain.People.Person> people, Guid[] sourceIds, CancellationToken cancellationToken)
    {
        var participants = new List<AnalysisPerson>();
        foreach (var person in people)
        {
            var factsQuery = dbContext.PersonFacts.AsNoTracking().Where(fact => fact.PersonId == person.Id)
                .OrderByDescending(fact => fact.CreatedAt).ThenBy(fact => fact.Id).Take(limits.FactsPerPerson);
            if (await factsQuery.AnyAsync(fact => fact.Value.Length > limits.FactValueCharacters || (fact.Source != null && fact.Source.Length > limits.FactSourceCharacters), cancellationToken))
                throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
            var facts = await factsQuery.Select(fact => new AnalysisFact(fact.Id, fact.Value, fact.Source)).ToListAsync(cancellationToken);
            var inferencesQuery = dbContext.PersonInferences.AsNoTracking().Where(inference => inference.PersonId == person.Id &&
                (inference.SourceInteractionId == null || (sourceIds.Contains(inference.SourceInteractionId.Value) &&
                    inference.SourceInteraction != null && inference.SourceInteraction.UserProfileId == userProfileId)))
                .OrderByDescending(inference => inference.CreatedAt).ThenBy(inference => inference.Id).Take(limits.InferencesPerPerson);
            if (await inferencesQuery.AnyAsync(inference => inference.Value.Length > limits.InferenceValueCharacters ||
                inference.Confidence < 0 || inference.Confidence > 1, cancellationToken))
                throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
            var inferences = await inferencesQuery.Select(inference => new AnalysisInference(inference.Id,
                inference.Value, inference.Confidence, inference.SourceInteractionId)).ToListAsync(cancellationToken);
            foreach (var field in new[] { person.DisplayName, person.Gender, person.Description, person.HowWeMet, person.Notes }) CheckText(field, limits.ProfileFieldCharacters);
            participants.Add(new AnalysisPerson(person.Id, person.DisplayName, person.Age, person.Gender, person.Description,
                person.RelationshipContext, person.HowWeMet, person.Notes, facts, inferences));
        }
        return participants;
    }

    private async Task<(IReadOnlyList<AnalysisEvidence> Evidence, IReadOnlyList<AiImageInput> Images)> LoadEvidenceAsync(
        Guid interactionId, IReadOnlyList<Guid>? selectedIds, CancellationToken cancellationToken)
    {
        using var operation = SocialTelemetryTelemetry.Start("analysis.load_evidence", cancellationToken);
        if (selectedIds is not null && (selectedIds.Count > limits.EvidenceCount || selectedIds.Distinct().Count() != selectedIds.Count))
            throw new AiProviderException(AiFailure.InvalidEvidenceSelection);
        var query = dbContext.InteractionAttachments.AsNoTracking().Where(attachment => attachment.InteractionId == interactionId);
        query = selectedIds is null
            ? query.Where(attachment => attachment.Status == AttachmentStatus.Ready)
            : query.Where(attachment => selectedIds.Contains(attachment.Id));
        if (await query.AnyAsync(attachment => attachment.TextContent != null &&
            attachment.TextContent.Length > limits.TextEvidenceCharacters, cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var attachments = await query.OrderBy(attachment => attachment.Id).Take(limits.EvidenceCount + 1).ToListAsync(cancellationToken);
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
                evidence.Add(await LoadTranscriptEvidenceAsync(attachment, cancellationToken));
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

    private async Task<AnalysisEvidence> LoadTranscriptEvidenceAsync(InteractionAttachment attachment, CancellationToken cancellationToken)
    {
        var textLimit = Math.Min(limits.TextEvidenceCharacters, transcriptOptions.Value.TextCharacters);
        var query = dbContext.AttachmentTranscripts.AsNoTracking().Where(transcript =>
            transcript.AttachmentId == attachment.Id && transcript.Attachment.InteractionId == attachment.InteractionId);
        // Reject before materialization; unused generated text must not enter corrected evidence.
        if (await query.AnyAsync(transcript => (transcript.CorrectedText ?? transcript.GeneratedText ?? string.Empty).Length > textLimit, cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var transcript = await query.Select(stored => new
        {
            Text = stored.CorrectedText ?? stored.GeneratedText, stored.Version, stored.ReviewStatus, stored.ReviewedAt,
            stored.SourceStorageKey, stored.SourceSha256, stored.Provider, stored.Model, stored.TranscriptionVersion
        }).SingleOrDefaultAsync(cancellationToken);
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
            if (total > uploadOptions.Value.AttachmentMaxBytes) throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
            hash.AppendData(buffer, 0, count);
        }
        if (total == 0) throw new AiProviderException(AiFailure.StaleTranscript);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

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
