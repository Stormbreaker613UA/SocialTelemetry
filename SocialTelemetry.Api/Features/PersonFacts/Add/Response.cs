namespace SocialTelemetry.Api.Features.PersonFacts.Add;

public sealed record Response(Guid Id, Guid PersonId, string Value, string? Source, DateTimeOffset CreatedAt);
