using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.GetExternalConnection;

public sealed class Endpoint(AppDbContext database, LocalProfileOperation operation) : Endpoint<Request, ExternalConnectionResponse>
{
    public override void Configure()
    {
        Get("/user-profile/external-connections/{connectionId}");
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

        var connection = await database.UserProfileExternalConnections.Where(connection => connection.UserProfileId == operation.RequireOwner()).AsNoTracking().SingleOrDefaultAsync(connection =>
            connection.Id == request.ConnectionId && connection.UserProfileId == ownerId, cancellationToken);
        if (connection is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        await Send.OkAsync(new ExternalConnectionResponse(connection.Id, connection.UserProfileId,
            connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
            connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt), cancellationToken);
    }
}
