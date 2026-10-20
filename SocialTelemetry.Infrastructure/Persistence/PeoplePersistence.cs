using Microsoft.EntityFrameworkCore;
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

    public async Task<IReadOnlyList<Person>> ListPeopleAsync(Guid ownerId, bool? archived, CancellationToken cancellationToken) =>
        await database.People.AsNoTracking().Where(person => person.UserProfileId == ownerId)
            .Where(person => archived == null || (person.ArchivedAt != null) == archived)
            .OrderBy(person => person.DisplayName).ToListAsync(cancellationToken);

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
