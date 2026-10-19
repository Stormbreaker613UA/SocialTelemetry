using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class ProfileConnectionsPersistence(AppDbContext database) : IProfileConnectionsPersistence
{
    public Task<bool> PersonExistsAsync(Guid ownerId, Guid parentId, CancellationToken cancellationToken) =>
        database.People.AsNoTracking().AnyAsync(parent => parent.UserProfileId == ownerId && parent.Id == parentId, cancellationToken);

    public Task<PersonExternalConnection?> FindPersonConnectionAsync(Guid ownerId, Guid parentId, Guid connectionId, bool readOnly, CancellationToken cancellationToken)
    {
        var connections = readOnly ? database.PersonExternalConnections.AsNoTracking() : database.PersonExternalConnections;
        return connections.SingleOrDefaultAsync(connection => database.People.Any(person => person.Id == connection.PersonId && person.UserProfileId == ownerId) &&
            connection.PersonId == parentId && connection.Id == connectionId, cancellationToken);
    }

    public async Task<IReadOnlyList<PersonExternalConnection>> ListPersonConnectionsAsync(Guid ownerId, Guid parentId, CancellationToken cancellationToken) =>
        await database.PersonExternalConnections.AsNoTracking().Where(connection => database.People.Any(person => person.Id == connection.PersonId && person.UserProfileId == ownerId) && connection.PersonId == parentId)
            .OrderBy(connection => connection.Platform).ThenBy(connection => connection.Id).ToListAsync(cancellationToken);

    public async Task AddPersonConnectionAsync(PersonExternalConnection connection, CancellationToken cancellationToken)
    {
        database.PersonExternalConnections.Add(connection);
        await ExternalConnectionPersistence.SaveAsync(database, cancellationToken);
    }

    public async Task SavePersonConnectionAsync(PersonExternalConnection connection, CancellationToken cancellationToken)
    {
        await ExternalConnectionPersistence.SaveAsync(database, cancellationToken);
    }

    public async Task DeletePersonConnectionAsync(PersonExternalConnection connection, CancellationToken cancellationToken)
    {
        database.PersonExternalConnections.Remove(connection);
        await database.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> ProfileExistsAsync(Guid ownerId, Guid parentId, CancellationToken cancellationToken) =>
        database.UserProfiles.AsNoTracking().AnyAsync(parent => parent.Id == ownerId && parent.Id == parentId, cancellationToken);

    public Task<UserProfileExternalConnection?> FindProfileConnectionAsync(Guid ownerId, Guid parentId, Guid connectionId, bool readOnly, CancellationToken cancellationToken)
    {
        var connections = readOnly ? database.UserProfileExternalConnections.AsNoTracking() : database.UserProfileExternalConnections;
        return connections.SingleOrDefaultAsync(connection => connection.UserProfileId == ownerId &&
            connection.UserProfileId == parentId && connection.Id == connectionId, cancellationToken);
    }

    public async Task<IReadOnlyList<UserProfileExternalConnection>> ListProfileConnectionsAsync(Guid ownerId, Guid parentId, CancellationToken cancellationToken) =>
        await database.UserProfileExternalConnections.AsNoTracking().Where(connection => connection.UserProfileId == ownerId && connection.UserProfileId == parentId)
            .OrderBy(connection => connection.Platform).ThenBy(connection => connection.Id).ToListAsync(cancellationToken);

    public async Task AddProfileConnectionAsync(UserProfileExternalConnection connection, CancellationToken cancellationToken)
    {
        database.UserProfileExternalConnections.Add(connection);
        await ExternalConnectionPersistence.SaveAsync(database, cancellationToken);
    }

    public async Task SaveProfileConnectionAsync(UserProfileExternalConnection connection, CancellationToken cancellationToken)
    {
        await ExternalConnectionPersistence.SaveAsync(database, cancellationToken);
    }

    public async Task DeleteProfileConnectionAsync(UserProfileExternalConnection connection, CancellationToken cancellationToken)
    {
        database.UserProfileExternalConnections.Remove(connection);
        await database.SaveChangesAsync(cancellationToken);
    }

}
