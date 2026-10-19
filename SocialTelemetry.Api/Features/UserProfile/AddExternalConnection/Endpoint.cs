using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.AddExternalConnection;

public sealed class Endpoint(AppDbContext database, LocalProfileOperation operation) : Endpoint<Request, ExternalConnectionResponse>
{
    public override void Configure()
    {
        Post("/user-profile/external-connections");
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
        var connection = new UserProfileExternalConnection
        {
            Id = Guid.NewGuid(), UserProfileId = ownerId.Value,
            Platform = input.Platform, ExternalUserId = input.ExternalUserId, Handle = input.Handle,
            DisplayName = input.DisplayName, ProfileUrl = input.ProfileUrl,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        database.UserProfileExternalConnections.Add(connection);
        await ExternalConnectionPersistence.SaveAsync(database, cancellationToken);
        await Send.ResponseAsync(new ExternalConnectionResponse(connection.Id, connection.UserProfileId,
            connection.Platform, connection.ExternalUserId, connection.Handle, connection.DisplayName,
            connection.ProfileUrl, connection.CreatedAt, connection.UpdatedAt), 201, cancellationToken);
    }
}
