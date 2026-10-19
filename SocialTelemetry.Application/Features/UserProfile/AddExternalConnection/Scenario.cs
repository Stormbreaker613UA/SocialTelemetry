using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileConnections;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Application.Features.UserProfile.AddExternalConnection;

public sealed class Scenario(IProfileConnectionsPersistence persistence, LocalProfileOperation operation)
{
    public async Task<UserProfileExternalConnection> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        var parentId = ownerId;
        if (!await persistence.ProfileExistsAsync(ownerId, parentId, cancellationToken)) throw new NotFoundException();

        if (!request.TryNormalize(out var input))
        {
            throw new FeatureValidationException("Invalid external connection fields or profile URL.");
        }
        var connection = new UserProfileExternalConnection
        {
            Id = Guid.NewGuid(), UserProfileId = parentId,
            Platform = input.Platform, ExternalUserId = input.ExternalUserId, Handle = input.Handle,
            DisplayName = input.DisplayName, ProfileUrl = input.ProfileUrl,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        await persistence.AddProfileConnectionAsync(connection, cancellationToken);
        return connection;
    }
}
