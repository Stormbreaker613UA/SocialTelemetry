using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Profiles;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed partial class AppDbContext
{
    internal void RequireCleanLifecycleScope()
    {
        // Never save or discard caller-owned pending work as part of deleting a journal.
        if (ChangeTracker.HasChanges()) throw new ConflictException();
    }

    internal async Task DeleteOpenedLocalProfileAsync(Guid owner, Guid expectedVersion, Guid newVersion,
        CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is null || profileOperation?.Binding is not { } binding ||
            binding.UserProfileId != owner || binding.SelectionVersion != expectedVersion)
            throw new InvalidOperationException("Profile deletion requires the captured protected operation.");
        RequireCleanLifecycleScope();
        await LockAnalysisContextAsync(owner, cancellationToken);

        // Local admission coordinates this transaction. The private graph operations do not
        // read or change the singleton selector and cannot be called as an unguarded entry point.
        await ValidateOwnedProfileGraphDeletionAsync(owner, cancellationToken);
        await DeleteOwnedProfileDependentsAsync(owner, cancellationToken);
        await CloseLocalProfileSelectionAsync(owner, expectedVersion, newVersion, cancellationToken);
        await DeleteOwnedProfileJournalAsync(owner, cancellationToken);
        await QueueProfileDeletionCleanupAsync(owner, cancellationToken);
    }

    private async Task ValidateOwnedProfileGraphDeletionAsync(Guid owner, CancellationToken cancellationToken)
    {
        if (!await UserProfiles.AnyAsync(profile => profile.Id == owner, cancellationToken)) throw new NotFoundException();
        await RefuseForeignCascadesAsync(owner, cancellationToken);
    }

    private async Task DeleteOwnedProfileDependentsAsync(Guid owner, CancellationToken cancellationToken)
    {
        // Bulk changes are deliberate here: the normal account save forbids journal changes.
        // All commands remain in this one guarded transaction, including the cleanup receipt.
        await Users.Where(account => account.UserProfileId == owner).ExecuteDeleteAsync(cancellationToken);
        await Set<ProfileAiSelection>().Where(selection => selection.UserProfileId == owner).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task CloseLocalProfileSelectionAsync(Guid owner, Guid expectedVersion, Guid newVersion,
        CancellationToken cancellationToken)
    {
        var changed = await Set<LocalProfileSelection>().Where(selection => selection.Id == 1 &&
                selection.UserProfileId == owner && selection.Version == expectedVersion)
            .ExecuteUpdateAsync(update => update.SetProperty(selection => selection.UserProfileId, (Guid?)null)
                .SetProperty(selection => selection.AccessGrantId, (Guid?)null)
                .SetProperty(selection => selection.Version, newVersion)
                .SetProperty(selection => selection.LegacySingleProfileAccess, false), cancellationToken);
        if (changed != 1) throw new ConflictException();
    }

    private async Task DeleteOwnedProfileJournalAsync(Guid owner, CancellationToken cancellationToken)
    {
        if (await UserProfiles.Where(profile => profile.Id == owner).ExecuteDeleteAsync(cancellationToken) != 1)
            throw new ConflictException();
    }

    private async Task QueueProfileDeletionCleanupAsync(Guid owner, CancellationToken cancellationToken)
    {
        var marker = new ProfileDeletionCleanupMarker { UserProfileId = owner };
        Add(marker);
        // Use the bool overload explicitly: the token-only EF overload dispatches back to
        // our coordinated virtual save, whose admission this deletion has intentionally closed.
        try { await base.SaveChangesAsync(true, cancellationToken); }
        finally { Entry(marker).State = EntityState.Detached; }
    }

    private async Task RefuseForeignCascadesAsync(Guid owner, CancellationToken cancellationToken)
    {
        var foreignParticipant = await InteractionParticipants.AnyAsync(participant =>
            (participant.Interaction.UserProfileId == owner && participant.Person.UserProfileId != owner) ||
            (participant.Person.UserProfileId == owner && participant.Interaction.UserProfileId != owner), cancellationToken);
        var foreignInference = await PersonInferences.AnyAsync(inference => inference.SourceInteraction != null &&
            ((inference.Person.UserProfileId == owner && inference.SourceInteraction.UserProfileId != owner) ||
             (inference.SourceInteraction.UserProfileId == owner && inference.Person.UserProfileId != owner)), cancellationToken);
        var foreignSuggestion = await SuggestedProfileUpdates.AnyAsync(suggestion =>
            (suggestion.Person.UserProfileId == owner && suggestion.InteractionAnalysis.Interaction.UserProfileId != owner) ||
            (suggestion.InteractionAnalysis.Interaction.UserProfileId == owner && suggestion.Person.UserProfileId != owner), cancellationToken);
        var foreignMembership = await PersonGroupMemberships.AnyAsync(membership =>
            (membership.Group.UserProfileId == owner || membership.Person.UserProfileId == owner) &&
            membership.Group.UserProfileId != membership.Person.UserProfileId, cancellationToken);
        if (foreignParticipant || foreignInference || foreignSuggestion || foreignMembership) throw new ConflictException();
    }
}
