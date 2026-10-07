using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.UserProfile.GetAvatar;

public sealed class Endpoint(ProfileAvatarService avatars) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/user-profile/avatar");
        AllowAnonymous();

    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var owner = await avatars.FindOwnerAsync(null, cancellationToken, readOnly: true);
        var avatar = await avatars.OpenAsync(owner, cancellationToken);
        await using var content = avatar.Content;
        HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.StreamAsync(content, contentType: avatar.MimeType, cancellation: cancellationToken);
    }
}
