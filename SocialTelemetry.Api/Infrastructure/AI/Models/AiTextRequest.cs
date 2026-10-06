namespace SocialTelemetry.Api.Infrastructure.AI.Models;

public sealed record AiTextRequest(string Instructions, string Input)
{
    public IReadOnlyList<AiImageInput> Images { get; init; } = [];
    public AiStructuredOutput? StructuredOutput { get; init; }
    public AiSelectedModel? SelectedModel { get; init; }
}

public sealed record AiImageInput(Guid EvidenceId, string MimeType, ReadOnlyMemory<byte> Bytes);
public sealed record AiStructuredOutput(string Name, System.Text.Json.JsonElement Schema);
public sealed record AiTextResponse(string Text, string ProviderId, string RequestedModel, string? ReturnedModel)
{
    public string Model => string.IsNullOrWhiteSpace(ReturnedModel) ? RequestedModel : ReturnedModel;
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid ExecutionId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationToken LifetimeCancellationToken { get; init; }
}
