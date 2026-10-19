namespace SocialTelemetry.Api.Features.AiConnection;

public static class AiConnectionRoutes
{
    public const string CallbackPath = "/ai-connection/chatgpt/callback";
}

// Supplied by the composition root from the existing validated provider configuration.
public sealed record LocalAiTransport(Uri CallbackUri);
