namespace SocialTelemetry.Api.Infrastructure.AI.Models;

public sealed record AiTextRequest(string Instructions, string Input);
public sealed record AiTextResponse(string Text, string ProviderId, string RequestedModel, string? ReturnedModel)
{
    public string Model => string.IsNullOrWhiteSpace(ReturnedModel) ? RequestedModel : ReturnedModel;
}
