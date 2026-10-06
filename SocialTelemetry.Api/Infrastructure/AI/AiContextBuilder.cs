using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.AI.Models;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class AiContextBuilder(AppDbContext dbContext, IAttachmentStorage storage)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BuiltAnalysisContext> BuildAsync(Guid interactionId, string? userQuestion,
        IReadOnlyList<Guid>? selectedAttachmentIds, CancellationToken cancellationToken)
    {
        CheckText(userQuestion, AnalysisLimits.QuestionCharacters);
        var interactionQuery = dbContext.Interactions.AsNoTracking().Where(stored => stored.Id == interactionId);
        if (await interactionQuery.AnyAsync(stored => stored.Title.Length > 500 || stored.Description.Length > 8_000 ||
            (stored.UserThoughts != null && stored.UserThoughts.Length > 4_000), cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var interaction = await interactionQuery.SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Interaction not found.");
        var userQuery = dbContext.UserProfiles.AsNoTracking().Where(profile => profile.Id == interaction.UserProfileId);
        if (await userQuery.AnyAsync(profile => profile.DisplayName.Length > 4_000 ||
            (profile.AboutMe != null && profile.AboutMe.Length > 4_000) ||
            (profile.CommunicationStyle != null && profile.CommunicationStyle.Length > 4_000) ||
            (profile.Goals != null && profile.Goals.Length > 4_000) ||
            (profile.Preferences != null && profile.Preferences.Length > 4_000) ||
            (profile.Boundaries != null && profile.Boundaries.Length > 4_000) ||
            (profile.AiInstructions != null && profile.AiInstructions.Length > 4_000), cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var user = await userQuery.SingleAsync(cancellationToken);
        var participantIds = await dbContext.InteractionParticipants.AsNoTracking()
            .Where(participant => participant.InteractionId == interactionId)
            .OrderBy(participant => participant.PersonId).Select(participant => participant.PersonId)
            .Take(AnalysisLimits.Participants + 1).ToListAsync(cancellationToken);
        if (participantIds.Count == 0 || participantIds.Count > AnalysisLimits.Participants)
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var peopleQuery = dbContext.People.AsNoTracking()
            .Where(person => participantIds.Contains(person.Id) && person.UserProfileId == interaction.UserProfileId);
        if (await peopleQuery.AnyAsync(person => person.DisplayName.Length > 4_000 ||
            (person.Gender != null && person.Gender.Length > 4_000) ||
            (person.Description != null && person.Description.Length > 4_000) ||
            (person.HowWeMet != null && person.HowWeMet.Length > 4_000) ||
            (person.Notes != null && person.Notes.Length > 4_000), cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var people = await peopleQuery.OrderBy(person => person.Id).ToListAsync(cancellationToken);
        if (people.Count != participantIds.Count) throw new AiProviderException(AiFailure.StaleContext);

        // Latest five earlier interactions with an overlapping participant; exclude interactions
        // containing any foreign/non-current participant so unrelated People never enter context.
        var historyQuery = dbContext.Interactions.AsNoTracking().Where(previous =>
            previous.UserProfileId == interaction.UserProfileId && previous.Id != interactionId &&
            previous.OccurredAt <= interaction.OccurredAt &&
            previous.Participants.Any(participant => participantIds.Contains(participant.PersonId)) &&
            !previous.Participants.Any(participant => !participantIds.Contains(participant.PersonId) ||
                participant.Person.UserProfileId != interaction.UserProfileId))
            .OrderByDescending(previous => previous.OccurredAt).ThenBy(previous => previous.Id)
            .Take(AnalysisLimits.PreviousInteractions);
        if (await historyQuery.AnyAsync(previous => previous.Title.Length > 500 ||
            previous.Description.Length > 8_000 || (previous.UserThoughts != null && previous.UserThoughts.Length > 4_000), cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var history = await historyQuery.Select(previous => new AnalysisInteraction(previous.Id, previous.Title,
            previous.Description, previous.UserThoughts, previous.OccurredAt,
            previous.Participants.OrderBy(participant => participant.PersonId).Select(participant => participant.PersonId).ToList()))
            .ToListAsync(cancellationToken);
        var sourceIds = history.Select(previous => previous.Id).Append(interactionId).ToArray();
        var participants = await LoadParticipantsAsync(interaction.UserProfileId, people, sourceIds, cancellationToken);
        foreach (var field in new[] { user.DisplayName, user.AboutMe, user.CommunicationStyle, user.Goals,
            user.Preferences, user.Boundaries, user.AiInstructions }) CheckText(field, 4_000);
        CheckText(interaction.Title, 500);
        CheckText(interaction.Description, 8_000);
        CheckText(interaction.UserThoughts, 4_000);
        var (evidence, images) = await LoadEvidenceAsync(interactionId, selectedAttachmentIds, cancellationToken);
        var context = new InteractionAnalysisContext(new AnalysisUser(user.Id, user.DisplayName, user.AboutMe,
            user.CommunicationStyle, user.Goals, user.Preferences, user.Boundaries, user.AiInstructions),
            new AnalysisInteraction(interaction.Id, interaction.Title, interaction.Description, interaction.UserThoughts,
                interaction.OccurredAt, participantIds), participants, history, evidence, userQuestion);
        var json = JsonSerializer.Serialize(context, JsonOptions);
        CheckText(json, AnalysisLimits.ContextCharacters);
        return new BuiltAnalysisContext(context, images, json, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    private async Task<IReadOnlyList<AnalysisPerson>> LoadParticipantsAsync(Guid userProfileId,
        IReadOnlyList<SocialTelemetry.Api.Domain.People.Person> people, Guid[] sourceIds, CancellationToken cancellationToken)
    {
        var participants = new List<AnalysisPerson>();
        foreach (var person in people)
        {
            var factsQuery = dbContext.PersonFacts.AsNoTracking().Where(fact => fact.PersonId == person.Id)
                .OrderByDescending(fact => fact.CreatedAt).ThenBy(fact => fact.Id).Take(AnalysisLimits.FactsPerPerson);
            if (await factsQuery.AnyAsync(fact => fact.Value.Length > 2_000 || (fact.Source != null && fact.Source.Length > 1_000), cancellationToken))
                throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
            var facts = await factsQuery.Select(fact => new AnalysisFact(fact.Id, fact.Value, fact.Source)).ToListAsync(cancellationToken);
            var inferencesQuery = dbContext.PersonInferences.AsNoTracking().Where(inference => inference.PersonId == person.Id &&
                (inference.SourceInteractionId == null || (sourceIds.Contains(inference.SourceInteractionId.Value) &&
                    inference.SourceInteraction != null && inference.SourceInteraction.UserProfileId == userProfileId)))
                .OrderByDescending(inference => inference.CreatedAt).ThenBy(inference => inference.Id).Take(AnalysisLimits.InferencesPerPerson);
            if (await inferencesQuery.AnyAsync(inference => inference.Value.Length > 2_000 ||
                inference.Confidence < 0 || inference.Confidence > 1, cancellationToken))
                throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
            var inferences = await inferencesQuery.Select(inference => new AnalysisInference(inference.Id,
                inference.Value, inference.Confidence, inference.SourceInteractionId)).ToListAsync(cancellationToken);
            foreach (var field in new[] { person.DisplayName, person.Gender, person.Description, person.HowWeMet, person.Notes }) CheckText(field, 4_000);
            participants.Add(new AnalysisPerson(person.Id, person.DisplayName, person.Age, person.Gender, person.Description,
                person.RelationshipContext, person.HowWeMet, person.Notes, facts, inferences));
        }
        return participants;
    }

    private async Task<(IReadOnlyList<AnalysisEvidence> Evidence, IReadOnlyList<AiImageInput> Images)> LoadEvidenceAsync(
        Guid interactionId, IReadOnlyList<Guid>? selectedIds, CancellationToken cancellationToken)
    {
        if (selectedIds is not null && (selectedIds.Count > AnalysisLimits.EvidenceCount || selectedIds.Distinct().Count() != selectedIds.Count))
            throw new AiProviderException(AiFailure.InvalidEvidenceSelection);
        var query = dbContext.InteractionAttachments.AsNoTracking().Where(attachment => attachment.InteractionId == interactionId);
        query = selectedIds is null
            ? query.Where(attachment => attachment.Status == AttachmentStatus.Ready)
            : query.Where(attachment => selectedIds.Contains(attachment.Id));
        if (await query.AnyAsync(attachment => attachment.TextContent != null &&
            attachment.TextContent.Length > AnalysisLimits.TextEvidenceCharacters, cancellationToken))
            throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
        var attachments = await query.OrderBy(attachment => attachment.Id).Take(AnalysisLimits.EvidenceCount + 1).ToListAsync(cancellationToken);
        if (attachments.Count > AnalysisLimits.EvidenceCount) throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
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
            if (attachment.Type is not (AttachmentType.Image or AttachmentType.Screenshot))
                throw new AiProviderException(AiFailure.UnsupportedEvidence);
            if (images.Count >= AnalysisLimits.ImageCount) throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
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
                if (buffer.Length + count > AnalysisLimits.ImageBytes || totalBytes + count > AnalysisLimits.TotalImageBytes)
                    throw new AiProviderException(AiFailure.EvidenceLimitExceeded);
                buffer.Write(chunk, 0, count);
                totalBytes += count;
            }
            var bytes = buffer.ToArray();
            if (!HasImageSignature(bytes, mimeType)) throw new AiProviderException(AiFailure.UnsupportedEvidence);
            images.Add(new AiImageInput(attachment.Id, mimeType, bytes));
            evidence.Add(new AnalysisEvidence(attachment.Id, attachment.Type, [], mimeType, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return (evidence, images);
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
