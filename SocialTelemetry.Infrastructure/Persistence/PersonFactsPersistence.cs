using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Features.PersonFacts;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class PersonFactsPersistence(AppDbContext database) : IPersonFactsPersistence
{
    public Task<bool> PersonExistsAsync(Guid ownerId, Guid personId, CancellationToken cancellationToken) =>
        database.People.AsNoTracking().AnyAsync(person => person.UserProfileId == ownerId && person.Id == personId, cancellationToken);

    public Task<PersonFact?> FindFactAsync(Guid ownerId, Guid personId, Guid factId, CancellationToken cancellationToken) =>
        database.PersonFacts.SingleOrDefaultAsync(fact => fact.Person.UserProfileId == ownerId &&
            fact.PersonId == personId && fact.Id == factId, cancellationToken);

    public async Task<IReadOnlyList<PersonFact>> ListFactsAsync(Guid ownerId, Guid personId, CancellationToken cancellationToken) =>
        await database.PersonFacts.AsNoTracking().Where(fact => fact.Person.UserProfileId == ownerId && fact.PersonId == personId)
            .OrderBy(fact => fact.CreatedAt).ToListAsync(cancellationToken);

    public async Task AddFactAsync(PersonFact fact, CancellationToken cancellationToken)
    {
        database.PersonFacts.Add(fact);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveFactAsync(PersonFact fact, CancellationToken cancellationToken) =>
        await database.SaveChangesAsync(cancellationToken);

    public async Task DeleteFactAsync(PersonFact fact, CancellationToken cancellationToken)
    {
        database.PersonFacts.Remove(fact);
        await database.SaveChangesAsync(cancellationToken);
    }
}
