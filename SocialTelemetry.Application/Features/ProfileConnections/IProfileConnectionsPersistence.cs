using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.ProfileConnections;

public interface IProfileConnectionsPersistence
{
    Task<bool> PersonExistsAsync(Guid ownerId, Guid parentId, CancellationToken cancellationToken);
    Task<PersonExternalConnection?> FindPersonConnectionAsync(Guid ownerId, Guid parentId, Guid connectionId, bool readOnly, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersonExternalConnection>> ListPersonConnectionsAsync(Guid ownerId, Guid parentId, CancellationToken cancellationToken);
    Task AddPersonConnectionAsync(PersonExternalConnection connection, CancellationToken cancellationToken);
    Task SavePersonConnectionAsync(PersonExternalConnection connection, CancellationToken cancellationToken);
    Task DeletePersonConnectionAsync(PersonExternalConnection connection, CancellationToken cancellationToken);
    Task<bool> ProfileExistsAsync(Guid ownerId, Guid parentId, CancellationToken cancellationToken);
    Task<UserProfileExternalConnection?> FindProfileConnectionAsync(Guid ownerId, Guid parentId, Guid connectionId, bool readOnly, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserProfileExternalConnection>> ListProfileConnectionsAsync(Guid ownerId, Guid parentId, CancellationToken cancellationToken);
    Task AddProfileConnectionAsync(UserProfileExternalConnection connection, CancellationToken cancellationToken);
    Task SaveProfileConnectionAsync(UserProfileExternalConnection connection, CancellationToken cancellationToken);
    Task DeleteProfileConnectionAsync(UserProfileExternalConnection connection, CancellationToken cancellationToken);
}
