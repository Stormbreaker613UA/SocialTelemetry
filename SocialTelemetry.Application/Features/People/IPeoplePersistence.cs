using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.People;

public interface IPeoplePersistence
{
    Task<bool> ProfileExistsAsync(Guid ownerId, Guid profileId, CancellationToken cancellationToken);
    Task<Person?> FindPersonAsync(Guid ownerId, Guid personId, bool readOnly, CancellationToken cancellationToken);
    Task<IReadOnlyList<Person>> ListPeopleAsync(Guid ownerId, bool? archived, CancellationToken cancellationToken);
    Task AddPersonAsync(Person person, CancellationToken cancellationToken);
    Task SavePersonAsync(Person person, CancellationToken cancellationToken);
    Task DeletePersonAsync(Person person, CancellationToken cancellationToken);
}

