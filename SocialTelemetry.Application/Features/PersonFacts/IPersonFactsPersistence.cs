using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonFacts;

public interface IPersonFactsPersistence
{
    Task<bool> PersonExistsAsync(Guid ownerId, Guid personId, CancellationToken cancellationToken);
    Task<PersonFact?> FindFactAsync(Guid ownerId, Guid personId, Guid factId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersonFact>> ListFactsAsync(Guid ownerId, Guid personId, CancellationToken cancellationToken);
    Task AddFactAsync(PersonFact fact, CancellationToken cancellationToken);
    Task SaveFactAsync(PersonFact fact, CancellationToken cancellationToken);
    Task DeleteFactAsync(PersonFact fact, CancellationToken cancellationToken);
}

