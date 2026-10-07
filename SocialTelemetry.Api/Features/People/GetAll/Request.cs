namespace SocialTelemetry.Api.Features.People.GetAll;

public sealed class Request
{
    public Guid UserProfileId { get; init; }
    public bool? Archived { get; init; }
}
