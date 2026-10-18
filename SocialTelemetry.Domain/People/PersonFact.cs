namespace SocialTelemetry.Api.Domain.People;

public sealed class PersonFact
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Person Person { get; set; } = null!;
}
