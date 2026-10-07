using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.GetExternalConnections;

public sealed class Endpoint(AppDbContext database) : Endpoint<Request, IReadOnlyList<ExternalConnectionResponse>>
{
    public override void Configure()
    {
        Get("/people/{personId}/external-connections");
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

        var connections = await database.PersonExternalConnections.AsNoTracking()
            .Where(connection => connection.PersonId == ownerId)
            .OrderBy(connection => connection.Platform).ThenBy(connection => connection.Id)
            .Select(connection => new ExternalConnectionResponse(connection.Id, connection.PersonId,
                connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
                connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt))
            .ToListAsync(cancellationToken);
        await Send.OkAsync(connections, cancellationToken);
    }
}
