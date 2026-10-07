using Microsoft.Extensions.Options;
using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.UserProfile.UploadAvatar;

public sealed class Endpoint(ProfileAvatarService avatars, IOptions<UploadOptions> options) : Endpoint<Request>
{
    public override void Configure()
    {
        Put("/user-profile/avatar");
        AllowAnonymous();
        AllowFileUploads();
        MaxRequestBodySize(options.Value.AvatarRequestBodyBytes);
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var owner = await avatars.FindOwnerAsync(null, cancellationToken);
        if (request.File is null || request.File.Length == 0 || request.File.Length > options.Value.AvatarMaxBytes)
        {
            AddError("A non-empty avatar within the configured upload limit is required.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }
        var mimeType = request.File.ContentType.ToLowerInvariant();
        await using var content = request.File.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > options.Value.AvatarMaxBytes)
            {
                AddError("Avatar exceeds the configured upload limit.");
                await Send.ErrorsAsync(400, cancellationToken);
                return;
            }
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
        var bytes = buffer.ToArray();
        if (!ProfileAvatarService.IsValidImage(bytes, mimeType))
        {
            AddError("Avatar must be PNG, JPEG, or WebP with matching content.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }
        await avatars.UploadAsync(owner, bytes, mimeType, cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
