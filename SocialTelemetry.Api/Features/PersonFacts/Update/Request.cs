namespace SocialTelemetry.Api.Features.PersonFacts.Update;

public sealed class Request
{
    public Guid PersonId { get; init; }
    public Guid Id { get; init; }
    public string Value { get; init; } = string.Empty;
    public string? Source { get; init; }
}
