namespace SocialTelemetry.Api.Features.PersonFacts.GetForPerson;

public sealed record Response(IReadOnlyList<PersonFactResponse> Facts);

public sealed record PersonFactResponse(Guid Id, string Value, string? Source, DateTimeOffset CreatedAt);
