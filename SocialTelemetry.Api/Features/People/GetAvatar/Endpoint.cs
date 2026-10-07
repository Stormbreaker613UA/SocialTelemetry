using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.People.GetAvatar;

public sealed class Endpoint(ProfileAvatarService avatars) : Endpoint<Request>
{
    public override void Configure()
    {
        Get("/people/{id}/avatar");
        AllowAnonymous();

    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var owner = await avatars.FindOwnerAsync(request.Id, cancellationToken, readOnly: true);
        var avatar = await avatars.OpenAsync(owner, cancellationToken);
        await using var content = avatar.Content;
        HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.StreamAsync(content, contentType: avatar.MimeType, cancellation: cancellationToken);
    }
}
