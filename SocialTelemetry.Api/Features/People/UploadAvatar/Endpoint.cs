using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Features.People.UploadAvatar;

public sealed class Endpoint(ProfileAvatarService avatars) : Endpoint<Request>
{
    public override void Configure()
    {
        Put("/people/{id}/avatar");
        AllowAnonymous();
        AllowFileUploads();
        MaxRequestBodySize(ProfileAvatarService.MaximumBytes + 1024 * 1024);
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var owner = await avatars.FindOwnerAsync(request.Id, cancellationToken);
        if (request.File is null || request.File.Length == 0 || request.File.Length > ProfileAvatarService.MaximumBytes)
        {
            AddError("A non-empty avatar up to 5 MiB is required.");
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
            if (buffer.Length + count > ProfileAvatarService.MaximumBytes)
            {
                AddError("Avatar exceeds 5 MiB.");
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
