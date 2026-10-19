using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Application.Features.Analysis;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using ProfileEntity = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class AnalysisContextQuery(AppDbContext database) : IAnalysisContextQuery
{
    public async Task<BoundedContextRead<Interaction>> InteractionAsync(Guid owner, Guid interactionId, AnalysisOptions limits, CancellationToken cancellationToken)
    {
        var query = database.Interactions.AsNoTracking().Where(stored => stored.Id == interactionId && stored.UserProfileId == owner);
        if (await query.AnyAsync(stored => stored.Title.Length > limits.InteractionTitleCharacters || stored.Description.Length > limits.InteractionDescriptionCharacters ||
            (stored.UserThoughts != null && stored.UserThoughts.Length > limits.UserThoughtsCharacters), cancellationToken))
            return new(true, null);
        return new(false, await query.SingleOrDefaultAsync(cancellationToken));
    }

    public async Task<BoundedContextRead<ProfileEntity>> UserAsync(Guid owner, AnalysisOptions limits, CancellationToken cancellationToken)
    {
        var query = database.UserProfiles.AsNoTracking().Where(profile => profile.Id == owner);
        if (await query.AnyAsync(profile => profile.DisplayName.Length > limits.ProfileFieldCharacters ||
            (profile.AboutMe != null && profile.AboutMe.Length > limits.ProfileFieldCharacters) ||
            (profile.CommunicationStyle != null && profile.CommunicationStyle.Length > limits.ProfileFieldCharacters) ||
            (profile.Goals != null && profile.Goals.Length > limits.ProfileFieldCharacters) ||
            (profile.Preferences != null && profile.Preferences.Length > limits.ProfileFieldCharacters) ||
            (profile.Boundaries != null && profile.Boundaries.Length > limits.ProfileFieldCharacters) ||
            (profile.AiInstructions != null && profile.AiInstructions.Length > limits.ProfileFieldCharacters), cancellationToken))
            return new(true, null);
        return new(false, await query.SingleAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> ParticipantIdsAsync(Guid owner, Guid interactionId, int maximumCount, CancellationToken cancellationToken) =>
        await database.InteractionParticipants.AsNoTracking()
            .Where(participant => participant.InteractionId == interactionId && participant.Interaction.UserProfileId == owner)
            .OrderBy(participant => participant.PersonId).Select(participant => participant.PersonId)
            .Take(maximumCount).ToListAsync(cancellationToken);

    public async Task<BoundedContextRead<IReadOnlyList<Person>>> PeopleAsync(Guid owner, IReadOnlyList<Guid> participantIds, AnalysisOptions limits, CancellationToken cancellationToken)
    {
        var query = database.People.AsNoTracking().Where(person => participantIds.Contains(person.Id) && person.UserProfileId == owner);
        if (await query.AnyAsync(person => person.DisplayName.Length > limits.ProfileFieldCharacters ||
            (person.Gender != null && person.Gender.Length > limits.ProfileFieldCharacters) ||
            (person.Description != null && person.Description.Length > limits.ProfileFieldCharacters) ||
            (person.HowWeMet != null && person.HowWeMet.Length > limits.ProfileFieldCharacters) ||
            (person.Notes != null && person.Notes.Length > limits.ProfileFieldCharacters), cancellationToken))
            return new(true, null);
        return new(false, await query.OrderBy(person => person.Id).ToListAsync(cancellationToken));
    }

    public async Task<BoundedContextRead<IReadOnlyList<AnalysisInteraction>>> HistoryAsync(Guid owner, Interaction interaction,
        IReadOnlyList<Guid> participantIds, AnalysisOptions limits, CancellationToken cancellationToken)
    {
        var query = database.Interactions.AsNoTracking().Where(previous =>
            previous.UserProfileId == owner && previous.Id != interaction.Id && previous.OccurredAt <= interaction.OccurredAt &&
            previous.Participants.Any(participant => participantIds.Contains(participant.PersonId)) &&
            !previous.Participants.Any(participant => !participantIds.Contains(participant.PersonId) || participant.Person.UserProfileId != owner))
            .OrderByDescending(previous => previous.OccurredAt).ThenBy(previous => previous.Id).Take(limits.PreviousInteractions);
        if (await query.AnyAsync(previous => previous.Title.Length > limits.InteractionTitleCharacters ||
            previous.Description.Length > limits.InteractionDescriptionCharacters ||
            (previous.UserThoughts != null && previous.UserThoughts.Length > limits.UserThoughtsCharacters), cancellationToken))
            return new(true, null);
        return new(false, await query.Select(previous => new AnalysisInteraction(previous.Id, previous.Title, previous.Description,
            previous.UserThoughts, previous.OccurredAt,
            previous.Participants.OrderBy(participant => participant.PersonId).Select(participant => participant.PersonId).ToList()))
            .ToListAsync(cancellationToken));
    }

    public async Task<BoundedContextRead<IReadOnlyList<AnalysisFact>>> FactsAsync(Guid owner, Guid personId, AnalysisOptions limits, CancellationToken cancellationToken)
    {
        var query = database.PersonFacts.AsNoTracking().Where(fact => fact.PersonId == personId && fact.Person.UserProfileId == owner)
            .OrderByDescending(fact => fact.CreatedAt).ThenBy(fact => fact.Id).Take(limits.FactsPerPerson);
        if (await query.AnyAsync(fact => fact.Value.Length > limits.FactValueCharacters ||
            (fact.Source != null && fact.Source.Length > limits.FactSourceCharacters), cancellationToken))
            return new(true, null);
        return new(false, await query.Select(fact => new AnalysisFact(fact.Id, fact.Value, fact.Source)).ToListAsync(cancellationToken));
    }

    public async Task<BoundedContextRead<IReadOnlyList<AnalysisInference>>> InferencesAsync(Guid owner, Guid personId, Guid[] sourceIds,
        AnalysisOptions limits, CancellationToken cancellationToken)
    {
        var query = database.PersonInferences.AsNoTracking().Where(inference => inference.PersonId == personId &&
            inference.Person.UserProfileId == owner &&
            (inference.SourceInteractionId == null || (sourceIds.Contains(inference.SourceInteractionId.Value) &&
                inference.SourceInteraction != null && inference.SourceInteraction.UserProfileId == owner)))
            .OrderByDescending(inference => inference.CreatedAt).ThenBy(inference => inference.Id).Take(limits.InferencesPerPerson);
        if (await query.AnyAsync(inference => inference.Value.Length > limits.InferenceValueCharacters ||
            inference.Confidence < 0 || inference.Confidence > 1, cancellationToken))
            return new(true, null);
        return new(false, await query.Select(inference => new AnalysisInference(inference.Id, inference.Value,
            inference.Confidence, inference.SourceInteractionId)).ToListAsync(cancellationToken));
    }

    public async Task<BoundedContextRead<IReadOnlyList<InteractionAttachment>>> AttachmentsAsync(Guid owner, Guid interactionId,
        IReadOnlyList<Guid>? selectedIds, AnalysisOptions limits, CancellationToken cancellationToken)
    {
        var query = database.InteractionAttachments.AsNoTracking()
            .Where(attachment => attachment.InteractionId == interactionId && attachment.Interaction.UserProfileId == owner);
        query = selectedIds is null ? query.Where(attachment => attachment.Status == AttachmentStatus.Ready)
            : query.Where(attachment => selectedIds.Contains(attachment.Id));
        if (await query.AnyAsync(attachment => attachment.TextContent != null &&
            attachment.TextContent.Length > limits.TextEvidenceCharacters, cancellationToken))
            return new(true, null);
        return new(false, await query.OrderBy(attachment => attachment.Id).Take(limits.EvidenceCount + 1).ToListAsync(cancellationToken));
    }

    public async Task<BoundedContextRead<TranscriptEvidence>> TranscriptAsync(Guid owner, Guid attachmentId, Guid interactionId,
        int textLimit, CancellationToken cancellationToken)
    {
        var query = database.AttachmentTranscripts.AsNoTracking().Where(transcript =>
            transcript.AttachmentId == attachmentId && transcript.Attachment.InteractionId == interactionId &&
            transcript.Attachment.Interaction.UserProfileId == owner);
        if (await query.AnyAsync(transcript => (transcript.CorrectedText ?? transcript.GeneratedText ?? string.Empty).Length > textLimit, cancellationToken))
            return new(true, null);
        // Project only effective text; never materialize unused generated content.
        return new(false, await query.Select(stored => new TranscriptEvidence(stored.CorrectedText ?? stored.GeneratedText,
            stored.Version, stored.ReviewStatus, stored.ReviewedAt, stored.SourceStorageKey, stored.SourceSha256,
            stored.Provider, stored.Model, stored.TranscriptionVersion)).SingleOrDefaultAsync(cancellationToken));
    }
}
