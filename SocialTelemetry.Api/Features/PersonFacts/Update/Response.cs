namespace SocialTelemetry.Api.Features.PersonFacts.Update;

public sealed record Response(Guid Id, Guid PersonId, string Value, string? Source, DateTimeOffset CreatedAt);
