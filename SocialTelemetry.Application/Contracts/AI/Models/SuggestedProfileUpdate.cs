namespace SocialTelemetry.Application.Contracts.AI.Models;

public sealed record SuggestedProfileUpdate(Guid PersonId, string Field, string SuggestedValue);
