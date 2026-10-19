namespace SocialTelemetry.Application.Features.UserProfile.Update;

public sealed class Input
{
    public string DisplayName { get; init; } = string.Empty;
    public string? AboutMe { get; init; }
    public string? CommunicationStyle { get; init; }
    public string? Goals { get; init; }
    public string? Preferences { get; init; }
    public string? Boundaries { get; init; }
    public string? AiInstructions { get; init; }
}
