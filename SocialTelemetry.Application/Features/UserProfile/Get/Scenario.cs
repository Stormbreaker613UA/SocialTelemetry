using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.UserProfile.Get;

public sealed class Scenario(IUserProfilePersistence persistence, LocalProfileOperation operation)
{
    public async Task<JournalProfile> ExecuteAsync(CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        return await persistence.FindProfileAsync(ownerId, true, cancellationToken) ?? throw new NotFoundException();
    }
}
