namespace SocialTelemetry.Application.Features.PersonFacts.Add;

public sealed class Input
{
    public Guid PersonId { get; init; }
    public string Value { get; init; } = string.Empty;
    public string? Source { get; init; }
}
