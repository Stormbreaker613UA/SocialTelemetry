using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.DeleteExternalConnection;

public sealed class Endpoint(AppDbContext database) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/user-profile/external-connections/{connectionId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var ownerId = await database.UserProfiles.AsNoTracking().OrderBy(profile => profile.Id)
            .Select(profile => (Guid?)profile.Id).FirstOrDefaultAsync(cancellationToken);
        if (ownerId is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var connection = await database.UserProfileExternalConnections.SingleOrDefaultAsync(connection =>
            connection.Id == request.ConnectionId && connection.UserProfileId == ownerId, cancellationToken);
        if (connection is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }
        database.UserProfileExternalConnections.Remove(connection);
        await database.SaveChangesAsync(cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
