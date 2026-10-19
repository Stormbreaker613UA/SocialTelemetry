using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.DeleteExternalConnection;

public sealed class Endpoint(AppDbContext database, LocalProfileOperation operation) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/user-profile/external-connections/{connectionId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var ownerId = await database.UserProfiles.Where(profile => profile.Id == operation.RequireOwner()).AsNoTracking()
            .Select(profile => (Guid?)profile.Id).FirstOrDefaultAsync(cancellationToken);
        if (ownerId is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var connection = await database.UserProfileExternalConnections.Where(connection => connection.UserProfileId == operation.RequireOwner()).SingleOrDefaultAsync(connection =>
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
