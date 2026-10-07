using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.GetExternalConnection;

public sealed class Endpoint(AppDbContext database) : Endpoint<Request, ExternalConnectionResponse>
{
    public override void Configure()
    {
        Get("/people/{personId}/external-connections/{connectionId}");
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

        var connection = await database.PersonExternalConnections.AsNoTracking().SingleOrDefaultAsync(connection =>
            connection.Id == request.ConnectionId && connection.PersonId == ownerId, cancellationToken);
        if (connection is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        await Send.OkAsync(new ExternalConnectionResponse(connection.Id, connection.PersonId,
            connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
            connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt), cancellationToken);
    }
}
