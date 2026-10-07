using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.UserProfile.DeleteAvatar;

public sealed class Endpoint(ProfileAvatarService avatars) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/user-profile/avatar");
        AllowAnonymous();

    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var owner = await avatars.FindOwnerAsync(null, cancellationToken);
        await avatars.DeleteAsync(owner, cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
