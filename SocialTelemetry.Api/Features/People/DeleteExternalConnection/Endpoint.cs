using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.People.DeleteExternalConnection;

public sealed class Endpoint(AppDbContext database) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/people/{personId}/external-connections/{connectionId}");
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

        var connection = await database.PersonExternalConnections.SingleOrDefaultAsync(connection =>
            connection.Id == request.ConnectionId && connection.PersonId == ownerId, cancellationToken);
        if (connection is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }
        database.PersonExternalConnections.Remove(connection);
        await database.SaveChangesAsync(cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
