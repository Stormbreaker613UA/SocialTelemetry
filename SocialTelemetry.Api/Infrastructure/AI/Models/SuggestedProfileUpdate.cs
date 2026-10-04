namespace SocialTelemetry.Api.Infrastructure.AI.Models;

public sealed record SuggestedProfileUpdate(Guid PersonId, string Field, string SuggestedValue);
