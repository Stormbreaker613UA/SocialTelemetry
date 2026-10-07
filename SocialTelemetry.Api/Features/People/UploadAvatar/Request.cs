namespace SocialTelemetry.Api.Features.People.UploadAvatar;

public sealed class Request
{
    public Guid Id { get; init; }
    public IFormFile? File { get; init; }
}
