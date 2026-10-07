using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.AddExternalConnection;

public sealed class Endpoint(AppDbContext database) : Endpoint<Request, ExternalConnectionResponse>
{
    public override void Configure()
    {
        Post("/people/{personId}/external-connections");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var ownerId = request.PersonId;
        if (!await database.People.AsNoTracking().AnyAsync(person => person.Id == ownerId, cancellationToken))
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }
        if (!request.TryNormalize(out var input))
        {
            AddError("Invalid external connection fields or profile URL.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }
        var connection = new PersonExternalConnection
        {
            Id = Guid.NewGuid(), PersonId = ownerId,
            Platform = input.Platform, ExternalUserId = input.ExternalUserId, Handle = input.Handle,
            DisplayName = input.DisplayName, ProfileUrl = input.ProfileUrl,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        database.PersonExternalConnections.Add(connection);
        await ExternalConnectionPersistence.SaveAsync(database, cancellationToken);
        await Send.ResponseAsync(new ExternalConnectionResponse(connection.Id, connection.PersonId,
            connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
            connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt), 201, cancellationToken);
    }
}
