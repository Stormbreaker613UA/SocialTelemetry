using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Tests.Infrastructure.AI;

public sealed class ChatGptAnalysisTransportTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("TEXT/EVENT-STREAM")]
    public async Task Completed_event_stream_is_valid_without_a_header_or_with_case_insensitive_media_type(string? mediaType)
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        app.Server.InferenceMediaType = mediaType;
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var result = await client.GenerateTextAsync(new AiTextRequest("Instructions", "Input"), Cancellation);
        Assert.Equal("private-output", result.Text);
    }

    [Theory]
    [InlineData(null, "{\"status\":\"completed\",\"output\":\"private-output\"}", AiFailure.IncompleteResponse)]
    [InlineData(null, "data: {\"type\":\"response.output_text.delta\",\"delta\":\"private-output\"}\n\n", AiFailure.IncompleteResponse)]
    [InlineData("application/json", "{\"status\":\"completed\"}", AiFailure.MalformedResponse)]
    public async Task Missing_header_does_not_allow_json_or_incomplete_output_to_become_success(
        string? mediaType, string body, AiFailure expected)
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        app.Server.InferenceMediaType = mediaType;
        app.Server.StreamBody = body;
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateTextAsync(new AiTextRequest("Instructions", "Input"), Cancellation));
        Assert.Equal(expected, exception.Failure);
        Assert.DoesNotContain("private-output", app.Logs.Text);
    }

    [Fact]
    public async Task Documented_model_supports_vision_and_strict_structured_output_wire_mapping()
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var selected = await client.GetSelectedModelAsync(Cancellation);
        Assert.True(selected.Supports(AiCapability.Text));
        Assert.True(selected.Supports(AiCapability.Vision));
        Assert.True(selected.Supports(AiCapability.StructuredOutput));
        Assert.False(selected.Supports((AiCapability)999));
        var imageId = Guid.NewGuid();
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
        var result = await client.GenerateTextAsync(new AiTextRequest("Application rules", "Untrusted context")
        {
            SelectedModel = selected,
            Images = [new AiImageInput(imageId, "image/png", bytes)],
            StructuredOutput = AnalysisContract.OutputContract()
        }, Cancellation);
        Assert.Equal("chatgpt-plan", result.ProviderId);
        Assert.Equal("gpt-5.6-sol", result.RequestedModel);
        Assert.NotNull(app.Server.LastInferenceBody);
        using var document = JsonDocument.Parse(app.Server.LastInferenceBody);
        var body = document.RootElement;
        var format = body.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        var content = body.GetProperty("input")[0].GetProperty("content");
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.Equal("Untrusted context", content[0].GetProperty("text").GetString());
        Assert.Contains(imageId.ToString(), content[1].GetProperty("text").GetString());
        Assert.Equal("input_image", content[2].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(bytes), content[2].GetProperty("image_url").GetString());
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.DoesNotContain("private-output", app.Logs.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_model_cannot_submit_unsupported_image_or_structured_output(bool image)
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        await app.SelectModelAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var request = image
            ? new AiTextRequest("Instructions", "Input") { Images = [new AiImageInput(Guid.NewGuid(), "image/png", new byte[] { 1 })] }
            : new AiTextRequest("Instructions", "Input") { StructuredOutput = AnalysisContract.OutputContract() };
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateTextAsync(request, Cancellation));
        Assert.Equal(AiFailure.CapabilityMissing, exception.Failure);
        Assert.Equal(0, app.Server.InferenceCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Disconnect_invalidates_in_flight_result_even_if_provider_ignores_cancellation_and_user_reconnects(
        bool ignoreCancellation, bool secondHost)
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        using var otherHost = secondHost ? new ChatGptTestApp(app) : null;
        var disconnectingHost = otherHost ?? app;
        app.Server.InferenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Server.ContinueInference = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Server.IgnoreInferenceCancellation = ignoreCancellation;
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var running = client.GenerateTextAsync(new AiTextRequest("Instructions", "Input"), Cancellation);
        await app.Server.InferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        var connection = disconnectingHost.Services.GetRequiredService<ChatGptConnection>();
        await connection.DisconnectAsync(Cancellation);
        // Reconnect uses a new generation. The old completed response cannot become valid again.
        await app.ConnectAsync();
        app.Server.ContinueInference.SetResult();
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => running);
        Assert.Equal(AiFailure.ExecutionInvalidated, exception.Failure);
        Assert.True((await connection.GetStatusAsync(Cancellation)).Connected);
        app.Server.InferenceStarted = null;
        app.Server.ContinueInference = null;
        var currentClient = client;
        Assert.Equal("private-output", (await currentClient.GenerateTextAsync(new AiTextRequest("Instructions", "Input"), Cancellation)).Text);
    }

    [Fact]
    public async Task Completed_result_is_invalidated_before_persistence_after_disconnect()
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var result = await client.GenerateTextAsync(new AiTextRequest("Instructions", "Input"), Cancellation);
        await app.Services.GetRequiredService<ChatGptConnection>().DisconnectAsync(Cancellation);
        Assert.True(result.LifetimeCancellationToken.IsCancellationRequested);
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => client.ValidateExecutionAsync(result, Cancellation));
        Assert.Equal(AiFailure.ExecutionInvalidated, exception.Failure);
    }

    [Fact]
    public async Task Caller_cancellation_during_inference_does_not_disconnect_the_account()
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        app.Server.InferenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Server.ContinueInference = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var running = client.GenerateTextAsync(new AiTextRequest("Instructions", "Input"), cancellation.Token);
        await app.Server.InferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True((await app.Services.GetRequiredService<ChatGptConnection>().GetStatusAsync(Cancellation)).Connected);
    }

    private static async Task ConnectAnalysisModelAsync(ChatGptTestApp app)
    {
        app.Server.ModelsBody = """{"models":[{"slug":"gpt-5.6-sol","display_name":"Test model","visibility":"list"}]}""";
        await app.ConnectAsync();
        await app.SelectModelAsync("gpt-5.6-sol");
    }
}
