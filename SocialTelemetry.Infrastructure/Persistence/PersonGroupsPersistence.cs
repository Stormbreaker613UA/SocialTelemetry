using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.PersonGroups;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class PersonGroupsPersistence(AppDbContext database) : IPersonGroupsPersistence
{
    public async Task<IReadOnlyList<PersonGroup>> ListAsync(Guid owner, CancellationToken cancellationToken) =>
        await database.PersonGroups.AsNoTracking().Where(group => group.UserProfileId == owner)
            .OrderBy(group => group.Name).ThenBy(group => group.Id).ToListAsync(cancellationToken);

    public async Task<PersonGroup> GetAsync(Guid owner, Guid groupId, CancellationToken cancellationToken) =>
        await database.PersonGroups.AsNoTracking().SingleOrDefaultAsync(group =>
            group.Id == groupId && group.UserProfileId == owner, cancellationToken) ?? throw new NotFoundException();

    public async Task<IReadOnlyList<GroupMember>> MembersAsync(Guid owner, Guid groupId, bool? archived, CancellationToken cancellationToken)
    {
        await GetAsync(owner, groupId, cancellationToken);
        return await database.PersonGroupMemberships.AsNoTracking().Where(link => link.GroupId == groupId &&
                link.Group.UserProfileId == owner && link.Person.UserProfileId == owner &&
                (archived == null || (link.Person.ArchivedAt != null) == archived))
            .OrderBy(link => link.Person.DisplayName).ThenBy(link => link.PersonId)
            .Select(link => new GroupMember(link.PersonId, link.Person.DisplayName, link.Person.IsFavorite, link.Person.ArchivedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task CreateAsync(PersonGroup group, CancellationToken cancellationToken)
    {
        database.RequireCleanLifecycleScope();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockAnalysisContextAsync(group.UserProfileId, cancellationToken);
        database.PersonGroups.Add(group);
        await database.SaveChangesAsync(cancellationToken);
        await database.Entry(group).ReloadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PersonGroup> RenameAsync(Guid owner, Guid groupId, Action<PersonGroup> rename, CancellationToken cancellationToken)
    {
        database.RequireCleanLifecycleScope();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockAnalysisContextAsync(owner, cancellationToken);
        var group = await LoadCurrentGroupAsync(owner, groupId, cancellationToken);
        rename(group);
        await database.SaveChangesAsync(cancellationToken);
        await database.Entry(group).ReloadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return group;
    }

    public async Task DeleteAsync(Guid owner, Guid groupId, CancellationToken cancellationToken)
    {
        database.RequireCleanLifecycleScope();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockAnalysisContextAsync(owner, cancellationToken);
        var group = await LoadCurrentGroupAsync(owner, groupId, cancellationToken);
        database.PersonGroups.Remove(group);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ChangeMembershipAsync(Guid owner, Guid groupId, Guid personId,
        Func<bool, bool> desiredMembership, CancellationToken cancellationToken)
    {
        database.RequireCleanLifecycleScope();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockAnalysisContextAsync(owner, cancellationToken);
        await GetAsync(owner, groupId, cancellationToken);
        if (!await database.People.AsNoTracking().AnyAsync(person => person.Id == personId && person.UserProfileId == owner, cancellationToken))
            throw new NotFoundException();
        var membership = await database.PersonGroupMemberships.AsNoTracking()
            .SingleOrDefaultAsync(link => link.GroupId == groupId && link.PersonId == personId, cancellationToken);
        // An unchanged tracked instance from an earlier operation must not resurrect a deleted pair.
        var previous = database.ChangeTracker.Entries<PersonGroupMembership>()
            .SingleOrDefault(entry => entry.Entity.GroupId == groupId && entry.Entity.PersonId == personId);
        if (previous is not null) previous.State = EntityState.Detached;
        var desired = desiredMembership(membership is not null);
        if (desired && membership is null)
            database.PersonGroupMemberships.Add(new PersonGroupMembership { GroupId = groupId, PersonId = personId });
        else if (!desired && membership is not null)
            database.PersonGroupMemberships.Remove(membership);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<PersonGroup> LoadCurrentGroupAsync(Guid owner, Guid groupId, CancellationToken cancellationToken)
    {
        var group = await database.PersonGroups.SingleOrDefaultAsync(current => current.Id == groupId && current.UserProfileId == owner, cancellationToken)
            ?? throw new NotFoundException();
        await database.Entry(group).ReloadAsync(cancellationToken);
        if (group.UserProfileId != owner || database.Entry(group).State == EntityState.Detached) throw new NotFoundException();
        return group;
    }
}
