namespace SocialTelemetry.Api.Features.PersonFacts.Delete;

public sealed class Request
{
    public Guid PersonId { get; init; }
    public Guid Id { get; init; }
}
