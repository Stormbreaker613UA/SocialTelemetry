namespace SocialTelemetry.Api.Features.UserProfile.Update;

public sealed record Response(
    Guid Id,
    string DisplayName,
    string? AboutMe,
    string? CommunicationStyle,
    string? Goals,
    string? Preferences,
    string? Boundaries,
    string? AiInstructions);
