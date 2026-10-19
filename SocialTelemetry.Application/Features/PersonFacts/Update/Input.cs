namespace SocialTelemetry.Application.Features.PersonFacts.Update;

public sealed class Input
{
    public Guid PersonId { get; init; }
    public Guid Id { get; init; }
    public string Value { get; init; } = string.Empty;
    public string? Source { get; init; }
}
