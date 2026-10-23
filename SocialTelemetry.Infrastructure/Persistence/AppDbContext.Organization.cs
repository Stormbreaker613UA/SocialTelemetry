using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed partial class AppDbContext
{
    // These changes require journal ownership protection but are not AI context sources.
    private EntityEntry[] OrganizationChanges() => ChangeTracker.Entries().Where(entry =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
        (entry.Entity is PersonGroup or PersonGroupMembership ||
         entry.Entity is Person && entry.State == EntityState.Modified && entry.Property(nameof(Person.IsFavorite)).IsModified))
        .ToArray();

    private async Task<HashSet<Guid>> ResolveOrganizationProfilesAsync(EntityEntry[] changes,
        HashSet<Guid> deletedProfiles, HashSet<Guid> deletedPeople, CancellationToken cancellationToken)
    {
        var owners = new HashSet<Guid>();
        var groupIds = new HashSet<Guid>();
        var personIds = new HashSet<Guid>(deletedPeople);
        foreach (var entry in changes)
        {
            if (entry.Entity is PersonGroup group)
            {
                AddScopeValues(entry, nameof(PersonGroup.UserProfileId), owners);
                groupIds.Add(group.Id);
            }
            else if (entry.Entity is PersonGroupMembership)
            {
                AddScopeValues(entry, nameof(PersonGroupMembership.GroupId), groupIds);
                AddScopeValues(entry, nameof(PersonGroupMembership.PersonId), personIds);
            }
        }
        if (groupIds.Count == 0 && personIds.Count == 0 && deletedProfiles.Count == 0) return owners;
        groupIds.UnionWith(await PersonGroups.AsNoTracking()
            .Where(group => groupIds.Contains(group.Id) || deletedProfiles.Contains(group.UserProfileId))
            .Select(group => group.Id).ToListAsync(cancellationToken));
        // Cascades can reach an untracked legacy link on either side.
        var links = await PersonGroupMemberships.AsNoTracking()
            .Where(link => groupIds.Contains(link.GroupId) || personIds.Contains(link.PersonId))
            .Select(link => new { link.GroupId, link.PersonId }).ToListAsync(cancellationToken);
        foreach (var link in links) { groupIds.Add(link.GroupId); personIds.Add(link.PersonId); }
        owners.UnionWith(await PersonGroups.AsNoTracking().Where(group => groupIds.Contains(group.Id))
            .Select(group => group.UserProfileId).ToListAsync(cancellationToken));
        owners.UnionWith(await People.AsNoTracking().Where(person => personIds.Contains(person.Id))
            .Select(person => person.UserProfileId).ToListAsync(cancellationToken));
        foreach (var entry in ChangeTracker.Entries<PersonGroup>().Where(entry => groupIds.Contains(entry.Entity.Id)))
            AddScopeValues(entry, nameof(PersonGroup.UserProfileId), owners);
        foreach (var entry in ChangeTracker.Entries<Person>().Where(entry => personIds.Contains(entry.Entity.Id)))
            AddScopeValues(entry, nameof(Person.UserProfileId), owners);
        if (profileOperation?.Binding is { } binding && owners.Any(owner => owner != binding.UserProfileId))
            throw new ConflictException();
        return owners;
    }

    private async Task ValidateOrganizationChangesAsync(EntityEntry[] changes, CancellationToken cancellationToken)
    {
        var groupIds = changes.Select(entry => entry.Entity).OfType<PersonGroup>().Select(group => group.Id).ToHashSet();
        var personIds = changes.Where(entry => entry.Entity is Person &&
                (entry.State == EntityState.Deleted || entry.Property(nameof(Person.UserProfileId)).IsModified))
            .Select(entry => ((Person)entry.Entity).Id).ToHashSet();
        var deletedOwners = changes.Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity).OfType<SocialTelemetry.Domain.Users.UserProfile>().Select(profile => profile.Id).ToArray();
        if (deletedOwners.Length > 0)
        {
            groupIds.UnionWith(await PersonGroups.AsNoTracking().Where(group => deletedOwners.Contains(group.UserProfileId))
                .Select(group => group.Id).ToListAsync(cancellationToken));
            personIds.UnionWith(await People.AsNoTracking().Where(person => deletedOwners.Contains(person.UserProfileId))
                .Select(person => person.Id).ToListAsync(cancellationToken));
        }
        foreach (var entry in changes.Where(entry => entry.Entity is PersonGroupMembership))
        {
            AddScopeValues(entry, nameof(PersonGroupMembership.GroupId), groupIds);
            AddScopeValues(entry, nameof(PersonGroupMembership.PersonId), personIds);
        }
        if (groupIds.Count == 0 && personIds.Count == 0) return;
        var links = await PersonGroupMemberships.AsNoTracking()
            .Where(link => groupIds.Contains(link.GroupId) || personIds.Contains(link.PersonId))
            .Select(link => new { link.GroupId, link.PersonId }).ToListAsync(cancellationToken);
        var pairs = links.Select(link => (link.GroupId, link.PersonId)).ToHashSet();
        foreach (var membership in changes.Select(entry => entry.Entity).OfType<PersonGroupMembership>())
            pairs.Add((membership.GroupId, membership.PersonId));
        if (pairs.Count == 0) return;
        groupIds.UnionWith(pairs.Select(pair => pair.GroupId));
        personIds.UnionWith(pairs.Select(pair => pair.PersonId));
        var groups = await PersonGroups.AsNoTracking().Where(group => groupIds.Contains(group.Id))
            .ToDictionaryAsync(group => group.Id, group => group.UserProfileId, cancellationToken);
        var people = await People.AsNoTracking().Where(person => personIds.Contains(person.Id))
            .ToDictionaryAsync(person => person.Id, person => person.UserProfileId, cancellationToken);
        foreach (var entry in ChangeTracker.Entries<PersonGroup>().Where(entry => groupIds.Contains(entry.Entity.Id)))
        {
            // Deleted ID-only stubs and unchanged tracked instances do not assert a new owner.
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified &&
                entry.Property(nameof(PersonGroup.UserProfileId)).IsModified)
                groups[entry.Entity.Id] = entry.Entity.UserProfileId;
        }
        foreach (var entry in ChangeTracker.Entries<Person>().Where(entry => personIds.Contains(entry.Entity.Id)))
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified &&
                entry.Property(nameof(Person.UserProfileId)).IsModified)
                people[entry.Entity.Id] = entry.Entity.UserProfileId;
        }
        foreach (var (groupId, personId) in pairs)
            if (!groups.TryGetValue(groupId, out var groupOwner) || !people.TryGetValue(personId, out var personOwner) || groupOwner != personOwner)
                throw new ConflictException();
    }
}
