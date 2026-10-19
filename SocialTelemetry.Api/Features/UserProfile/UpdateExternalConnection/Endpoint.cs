using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.UpdateExternalConnection;

public sealed class Endpoint(AppDbContext database, LocalProfileOperation operation) : Endpoint<Request, ExternalConnectionResponse>
{
    public override void Configure()
    {
        Put("/user-profile/external-connections/{connectionId}");
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
        if (!request.TryNormalize(out var input))
        {
            AddError("Invalid external connection fields or profile URL.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }
        var connection = await database.UserProfileExternalConnections.Where(connection => connection.UserProfileId == operation.RequireOwner()).SingleOrDefaultAsync(connection =>
            connection.Id == request.ConnectionId && connection.UserProfileId == ownerId, cancellationToken);
        if (connection is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }
        connection.Platform = input.Platform;
        connection.ExternalUserId = input.ExternalUserId;
        connection.Handle = input.Handle;
        connection.DisplayName = input.DisplayName;
        connection.ProfileUrl = input.ProfileUrl;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await ExternalConnectionPersistence.SaveAsync(database, cancellationToken);
        await Send.OkAsync(new ExternalConnectionResponse(connection.Id, connection.UserProfileId,
            connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
            connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt), cancellationToken);
    }
}
