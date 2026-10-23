using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class PeoplePersistence(AppDbContext database) : IPeoplePersistence
{
    public Task<bool> ProfileExistsAsync(Guid ownerId, Guid profileId, CancellationToken cancellationToken) =>
        database.UserProfiles.AsNoTracking().AnyAsync(profile => profile.Id == ownerId && profile.Id == profileId, cancellationToken);

    public Task<Person?> FindPersonAsync(Guid ownerId, Guid personId, bool readOnly, CancellationToken cancellationToken)
    {
        var people = readOnly ? database.People.AsNoTracking() : database.People;
        return people.SingleOrDefaultAsync(person => person.Id == personId && person.UserProfileId == ownerId, cancellationToken);
    }

    public async Task<IReadOnlyList<Person>> ListPeopleAsync(Guid ownerId, bool? archived, CancellationToken cancellationToken,
        bool? isFavorite = null, Guid? groupId = null)
    {
        if (groupId is { } requestedGroup && !await database.PersonGroups.AsNoTracking().AnyAsync(group =>
                group.Id == requestedGroup && group.UserProfileId == ownerId, cancellationToken))
            throw new NotFoundException();
        return await database.People.AsNoTracking().Where(person => person.UserProfileId == ownerId)
            .Where(person => archived == null || (person.ArchivedAt != null) == archived)
            .Where(person => isFavorite == null || person.IsFavorite == isFavorite)
            .Where(person => groupId == null || database.PersonGroupMemberships.Any(link => link.GroupId == groupId &&
                link.PersonId == person.Id && link.Group.UserProfileId == ownerId))
            .OrderBy(person => person.DisplayName).ToListAsync(cancellationToken);
    }

    public async Task<Person> SetFavoriteAsync(Guid ownerId, Guid personId, Action<Person> setFavorite, CancellationToken cancellationToken)
    {
        database.RequireCleanLifecycleScope();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.LockAnalysisContextAsync(ownerId, cancellationToken);
        var person = await FindPersonAsync(ownerId, personId, false, cancellationToken) ?? throw new NotFoundException();
        await database.Entry(person).ReloadAsync(cancellationToken);
        if (person.UserProfileId != ownerId || database.Entry(person).State == EntityState.Detached) throw new NotFoundException();
        setFavorite(person);
        await database.SaveChangesAsync(cancellationToken);
        await database.Entry(person).ReloadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return person;
    }

    public async Task AddPersonAsync(Person person, CancellationToken cancellationToken)
    {
        database.People.Add(person);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task SavePersonAsync(Person person, CancellationToken cancellationToken) =>
        await database.SaveChangesAsync(cancellationToken);

    public async Task DeletePersonAsync(Person person, CancellationToken cancellationToken)
    {
        database.People.Remove(person);
        await database.SaveChangesAsync(cancellationToken);
    }
}
