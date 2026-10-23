namespace SocialTelemetry.Api.Features.People.Create;

public sealed record Response(Guid Id, bool IsFavorite = false);
