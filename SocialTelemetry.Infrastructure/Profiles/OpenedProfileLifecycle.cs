using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Features.LocalProfiles;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Storage;

namespace SocialTelemetry.Infrastructure.Profiles;

public sealed class OpenedProfileLifecycle(AppDbContext database, LocalProfileOperation operation,
    LocalProfileLifetimes profileLifetimes, ProfileAiLifetimes aiLifetimes, LocalAccessManagement access,
    ProfileDeletionCleanup cleanup, ProfileAvatarCleanup avatars, IAttachmentStorage attachments,
    ILogger<OpenedProfileLifecycle> logger) : IOpenedProfileLifecycle
{
    public async Task<DeletedProfile> DeleteAsync(Guid userProfileId, Guid expectedVersion, string? currentPassword,
        CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        if (owner != userProfileId) throw new SocialTelemetry.Application.Exceptions.NotFoundException();
        database.RequireCleanLifecycleScope();
        using var admission = await operation.EnterAsync(cancellationToken);
        cancellationToken = admission.CancellationToken;
        await profileLifetimes.SelectionGate.WaitAsync(cancellationToken);
        CancellationTokenSource? retiredOwner = null;
        CancellationTokenSource? retiredAi = null;
        var nextVersion = Guid.NewGuid();
        string[] avatarKeys = [];
        string[] attachmentKeys = [];
        LocalAccessFailure? failure = null;
        try
        {
            await aiLifetimes.Changes.WaitAsync(cancellationToken);
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                await database.LockLocalProfileSelectionAsync(cancellationToken);
                var selected = await database.Set<LocalProfileSelection>().AsNoTracking().SingleAsync(cancellationToken);
                SelectionPolicy.CheckVersion(new(selected.UserProfileId, selected.Version, selected.LegacySingleProfileAccess), expectedVersion);
                using (await operation.EnterAsync(cancellationToken)) { }
                await database.LockAnalysisContextAsync(owner, cancellationToken);
                failure = await access.CheckDeletionPasswordAsync(owner, currentPassword, cancellationToken);
                if (failure is null)
                {
                    avatarKeys = await database.UserProfiles.Where(profile => profile.Id == owner && profile.AvatarStorageKey != null)
                        .Select(profile => profile.AvatarStorageKey!).Concat(database.People.Where(person => person.UserProfileId == owner &&
                            person.AvatarStorageKey != null).Select(person => person.AvatarStorageKey!)).Distinct().ToArrayAsync(cancellationToken);
                    attachmentKeys = await database.InteractionAttachments.Where(attachment => attachment.Interaction.UserProfileId == owner &&
                        attachment.StorageKey != null).Select(attachment => attachment.StorageKey!).Distinct().ToArrayAsync(cancellationToken);
                    await database.DeleteOpenedLocalProfileAsync(owner, expectedVersion, nextVersion, cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                if (failure is null)
                {
                    retiredOwner = profileLifetimes.Replace(nextVersion);
                    retiredAi = aiLifetimes.Publish(owner, Guid.NewGuid());
                }
            }
            finally { aiLifetimes.Changes.Release(); }
        }
        finally
        {
            profileLifetimes.SelectionGate.Release();
            profileLifetimes.CancelRetired(retiredOwner);
            ProfileAiLifetimes.Cancel(retiredAi);
        }
        if (failure is { } reason) throw new LocalAccessException(reason);

        // This operation just cancelled its own admission. Cleanup must not relabel its committed winner.
        var pending = !await cleanup.TryCleanAsync(owner, CancellationToken.None);
        pending |= !await avatars.DeleteUnreferencedAsync(database, avatarKeys);
        foreach (var key in attachmentKeys)
        {
            try
            {
                if (await AttachmentFileCleanup.IsReferencedAsync(database, key, CancellationToken.None)) continue;
                await attachments.DeleteAsync(key, CancellationToken.None);
                await attachments.DeleteStagedAsync(key, CancellationToken.None);
            }
            catch (Exception exception)
            {
                pending = true;
                logger.LogWarning("Deleted profile media cleanup deferred ({ExceptionType})", exception.GetType().Name);
            }
        }
        return new(null, nextVersion, pending);
    }
}
