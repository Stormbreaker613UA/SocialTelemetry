using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.GetExternalConnections;

public sealed class Endpoint(AppDbContext database) : EndpointWithoutRequest<IReadOnlyList<ExternalConnectionResponse>>
{
    public override void Configure()
    {
        Get("/user-profile/external-connections");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var ownerId = await database.UserProfiles.AsNoTracking().OrderBy(profile => profile.Id)
            .Select(profile => (Guid?)profile.Id).FirstOrDefaultAsync(cancellationToken);
        if (ownerId is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var connections = await database.UserProfileExternalConnections.AsNoTracking()
            .Where(connection => connection.UserProfileId == ownerId)
            .OrderBy(connection => connection.Platform).ThenBy(connection => connection.Id)
            .Select(connection => new ExternalConnectionResponse(connection.Id, connection.UserProfileId,
                connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
                connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt))
            .ToListAsync(cancellationToken);
        await Send.OkAsync(connections, cancellationToken);
    }
}
