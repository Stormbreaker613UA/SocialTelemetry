namespace SocialTelemetry.Api.Features.UserProfile.Get;

public sealed record Response(
    Guid Id,
    string DisplayName,
    string? AboutMe,
    string? CommunicationStyle,
    string? Goals,
    string? Preferences,
    string? Boundaries,
    string? AiInstructions);
