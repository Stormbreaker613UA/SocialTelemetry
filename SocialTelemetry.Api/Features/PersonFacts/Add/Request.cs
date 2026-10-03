namespace SocialTelemetry.Api.Features.PersonFacts.Add;

public sealed class Request
{
    public Guid PersonId { get; init; }
    public string Value { get; init; } = string.Empty;
    public string? Source { get; init; }
}
