using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Users;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

public sealed partial class AppDbContext
{
    public Task LockAttachmentAnalysisContextAsync(Guid attachmentId, Guid interactionId, CancellationToken cancellationToken) =>
        LockResolvedProfilesAsync(async token => (await Interactions.AsNoTracking()
            .Where(interaction => interaction.Id == interactionId || InteractionAttachments.Any(attachment =>
                attachment.Id == attachmentId && attachment.InteractionId == interaction.Id))
            .Select(interaction => interaction.UserProfileId).ToListAsync(token)).ToHashSet(), cancellationToken);

    private Task LockAnalysisSourceChangesAsync(EntityEntry[] changes, CancellationToken cancellationToken) =>
        LockResolvedProfilesAsync(token => ResolveAnalysisProfilesAsync(changes, token), cancellationToken);

    private async Task LockResolvedProfilesAsync(Func<CancellationToken, Task<HashSet<Guid>>> resolveProfiles,
        CancellationToken cancellationToken)
    {
        var transaction = Database.CurrentTransaction
            ?? throw new InvalidOperationException("Analysis context protection requires a transaction.");
        var profiles = await resolveProfiles(cancellationToken);
        while (true)
        {
            await transaction.CreateSavepointAsync("AnalysisContextScopes", cancellationToken);
            try
            {
                foreach (var profileId in profiles.Order())
                    await LockAnalysisContextAsync(profileId, cancellationToken);

                // A parent may have moved while we waited. Resolve again under protection.
                // Retry the whole ordered set instead of appending a lower key and risking deadlock.
                var currentProfiles = await resolveProfiles(cancellationToken);
                if (currentProfiles.IsSubsetOf(profiles))
                {
                    await transaction.ReleaseSavepointAsync("AnalysisContextScopes", cancellationToken);
                    return;
                }
                profiles.UnionWith(currentProfiles);
                await transaction.RollbackToSavepointAsync("AnalysisContextScopes", cancellationToken);
                await transaction.ReleaseSavepointAsync("AnalysisContextScopes", cancellationToken);
            }
            catch
            {
                await transaction.RollbackToSavepointAsync("AnalysisContextScopes", CancellationToken.None);
                await transaction.ReleaseSavepointAsync("AnalysisContextScopes", CancellationToken.None);
                throw;
            }
        }
    }

    private async Task<HashSet<Guid>> ResolveAnalysisProfilesAsync(EntityEntry[] changes, CancellationToken cancellationToken)
    {
        var profiles = new HashSet<Guid>();
        var personIds = new HashSet<Guid>();
        var interactionIds = new HashSet<Guid>();
        var transcriptAttachmentIds = new HashSet<Guid>();
        var deletedProfiles = new HashSet<Guid>();
        var deletedPeople = new HashSet<Guid>();
        var deletedInteractions = new HashSet<Guid>();
        foreach (var entry in changes)
        {
            switch (entry.Entity)
            {
                case UserProfile profile:
                    profiles.Add(profile.Id);
                    if (entry.State == EntityState.Deleted) deletedProfiles.Add(profile.Id);
                    break;
                case Person person:
                    AddScopeValues(entry, nameof(Person.UserProfileId), profiles);
                    personIds.Add(person.Id);
                    if (entry.State == EntityState.Deleted) deletedPeople.Add(person.Id);
                    break;
                case Interaction interaction:
                    AddScopeValues(entry, nameof(Interaction.UserProfileId), profiles);
                    interactionIds.Add(interaction.Id);
                    if (entry.State == EntityState.Deleted) deletedInteractions.Add(interaction.Id);
                    break;
                case PersonFact:
                    AddScopeValues(entry, nameof(PersonFact.PersonId), personIds);
                    break;
                case PersonInference:
                    AddScopeValues(entry, nameof(PersonInference.PersonId), personIds);
                    AddScopeValues(entry, nameof(PersonInference.SourceInteractionId), interactionIds);
                    break;
                case InteractionParticipant:
                    AddScopeValues(entry, nameof(InteractionParticipant.PersonId), personIds);
                    AddScopeValues(entry, nameof(InteractionParticipant.InteractionId), interactionIds);
                    break;
                case InteractionAttachment:
                    AddScopeValues(entry, nameof(InteractionAttachment.InteractionId), interactionIds);
                    break;
                case AttachmentTranscript:
                    AddScopeValues(entry, nameof(AttachmentTranscript.AttachmentId), transcriptAttachmentIds);
                    break;
            }
        }

        // Detached deletes and stale tracked children may not hold their current database parent.
        if (transcriptAttachmentIds.Count > 0)
        {
            interactionIds.UnionWith(await InteractionAttachments.AsNoTracking()
                .Where(attachment => transcriptAttachmentIds.Contains(attachment.Id))
                .Select(attachment => attachment.InteractionId).ToListAsync(cancellationToken));
            foreach (var attachment in ChangeTracker.Entries<InteractionAttachment>()
                .Where(entry => transcriptAttachmentIds.Contains(entry.Entity.Id)))
                AddScopeValues(attachment, nameof(InteractionAttachment.InteractionId), interactionIds);
        }
        var factIds = changes.Where(entry => entry.State != EntityState.Added).Select(entry => entry.Entity)
            .OfType<PersonFact>().Select(fact => fact.Id).ToArray();
        if (factIds.Length > 0)
            personIds.UnionWith(await PersonFacts.AsNoTracking().Where(fact => factIds.Contains(fact.Id))
                .Select(fact => fact.PersonId).ToListAsync(cancellationToken));
        var inferenceIds = changes.Where(entry => entry.State != EntityState.Added).Select(entry => entry.Entity)
            .OfType<PersonInference>().Select(inference => inference.Id).ToArray();
        if (inferenceIds.Length > 0)
        {
            var parents = await PersonInferences.AsNoTracking().Where(inference => inferenceIds.Contains(inference.Id))
                .Select(inference => new { inference.PersonId, inference.SourceInteractionId }).ToListAsync(cancellationToken);
            foreach (var parent in parents)
            {
                personIds.Add(parent.PersonId);
                if (parent.SourceInteractionId is Guid sourceId) interactionIds.Add(sourceId);
            }
        }
        var attachmentIds = changes.Where(entry => entry.State != EntityState.Added).Select(entry => entry.Entity)
            .OfType<InteractionAttachment>().Select(attachment => attachment.Id).ToArray();
        if (attachmentIds.Length > 0)
            interactionIds.UnionWith(await InteractionAttachments.AsNoTracking().Where(attachment => attachmentIds.Contains(attachment.Id))
                .Select(attachment => attachment.InteractionId).ToListAsync(cancellationToken));

        // Database cascades can remove untracked children, including legacy cross-profile links.
        if (deletedProfiles.Count > 0)
        {
            deletedPeople.UnionWith(await People.AsNoTracking().Where(person => deletedProfiles.Contains(person.UserProfileId))
                .Select(person => person.Id).ToListAsync(cancellationToken));
            deletedInteractions.UnionWith(await Interactions.AsNoTracking().Where(interaction => deletedProfiles.Contains(interaction.UserProfileId))
                .Select(interaction => interaction.Id).ToListAsync(cancellationToken));
        }
        personIds.UnionWith(deletedPeople);
        interactionIds.UnionWith(deletedInteractions);
        var sourcePersonIds = deletedPeople.ToArray();
        var sourceInteractionIds = deletedInteractions.ToArray();
        if (sourcePersonIds.Length > 0)
            interactionIds.UnionWith(await InteractionParticipants.AsNoTracking()
                .Where(participant => sourcePersonIds.Contains(participant.PersonId))
                .Select(participant => participant.InteractionId).ToListAsync(cancellationToken));
        if (sourceInteractionIds.Length > 0)
            personIds.UnionWith(await PersonInferences.AsNoTracking()
                .Where(inference => inference.SourceInteractionId != null && sourceInteractionIds.Contains(inference.SourceInteractionId.Value))
                .Select(inference => inference.PersonId).ToListAsync(cancellationToken));

        if (personIds.Count > 0)
        {
            profiles.UnionWith(await People.AsNoTracking().Where(person => personIds.Contains(person.Id))
                .Select(person => person.UserProfileId).ToListAsync(cancellationToken));
            foreach (var person in ChangeTracker.Entries<Person>().Where(entry => personIds.Contains(entry.Entity.Id)))
                AddScopeValues(person, nameof(Person.UserProfileId), profiles);
        }
        if (interactionIds.Count > 0)
        {
            profiles.UnionWith(await Interactions.AsNoTracking().Where(interaction => interactionIds.Contains(interaction.Id))
                .Select(interaction => interaction.UserProfileId).ToListAsync(cancellationToken));
            foreach (var interaction in ChangeTracker.Entries<Interaction>().Where(entry => interactionIds.Contains(entry.Entity.Id)))
                AddScopeValues(interaction, nameof(Interaction.UserProfileId), profiles);
        }
        return profiles;
    }

    private static void AddScopeValues(EntityEntry entry, string propertyName, HashSet<Guid> identifiers)
    {
        var property = entry.Property(propertyName);
        if (property.CurrentValue is Guid current && current != Guid.Empty) identifiers.Add(current);
        if (entry.State != EntityState.Added && property.OriginalValue is Guid original && original != Guid.Empty)
            identifiers.Add(original);
    }
}
