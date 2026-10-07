using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.People.DeleteAvatar;

public sealed class Endpoint(ProfileAvatarService avatars) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/people/{id}/avatar");
        AllowAnonymous();

    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var owner = await avatars.FindOwnerAsync(request.Id, cancellationToken);
        await avatars.DeleteAsync(owner, cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
