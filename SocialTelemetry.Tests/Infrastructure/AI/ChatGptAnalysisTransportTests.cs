using SocialTelemetry.Application.Features.Analysis;
using AnalysisContract = SocialTelemetry.Application.Features.Analysis.AnalyzeInteraction.AnalysisContract;
using SocialTelemetry.Application.Contracts.AI;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Application.Contracts.AI.Models;

namespace SocialTelemetry.Tests.Infrastructure.AI;

public sealed class ChatGptAnalysisTransportTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("text/event-stream")]
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
    [InlineData(null, "arbitrary text", AiFailure.IncompleteResponse)]
    [InlineData(null, "<html><body>Provider unavailable</body></html>", AiFailure.IncompleteResponse)]
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

    [Theory]
    [InlineData(null, "", false, AiFailure.IncompleteResponse)]
    [InlineData("text/event-stream", "", false, AiFailure.IncompleteResponse)]
    [InlineData(null, "\n", false, AiFailure.IncompleteResponse)]
    [InlineData("text/event-stream", "\n", false, AiFailure.IncompleteResponse)]
    [InlineData(null, "", true, AiFailure.IncompleteResponse)]
    [InlineData("text/event-stream", "", true, AiFailure.IncompleteResponse)]
    [InlineData(null, "\n\n", true, AiFailure.MalformedResponse)]
    [InlineData("text/event-stream", "\n\n", true, AiFailure.MalformedResponse)]
    public async Task Incomplete_terminal_event_cannot_complete_inference(
        string? mediaType, string delimiter, bool truncatedJson, AiFailure expected)
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        app.Server.InferenceMediaType = mediaType;
        var terminalEvent = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}";
        if (truncatedJson) terminalEvent = terminalEvent[..^2];
        app.Server.StreamBody = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"private-output\"}\n\n" + terminalEvent + delimiter;

        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateTextAsync(new AiTextRequest("Instructions", "Input"), Cancellation));
        Assert.Equal(expected, exception.Failure);
        Assert.DoesNotContain("private-output", app.Logs.Text);
    }

    [Theory]
    [InlineData("refusal-delta", true, AiFailure.ProviderRejected)]
    [InlineData("refusal-done", false, AiFailure.ProviderRejected)]
    [InlineData("item-added", true, AiFailure.MalformedResponse)]
    [InlineData("item-done", false, AiFailure.MalformedResponse)]
    [InlineData("part-refusal", false, AiFailure.ProviderRejected)]
    [InlineData("part-tool", false, AiFailure.MalformedResponse)]
    [InlineData("tool-arguments", false, AiFailure.MalformedResponse)]
    [InlineData("tool-progress", true, AiFailure.MalformedResponse)]
    [InlineData("terminal-refusal", false, AiFailure.ProviderRejected)]
    [InlineData("terminal-tool", false, AiFailure.MalformedResponse)]
    [InlineData("terminal-content", false, AiFailure.MalformedResponse)]
    [InlineData("terminal-direct-content", false, AiFailure.ProviderRejected)]
    public async Task Structured_text_cannot_hide_explicit_refusal_or_unsupported_output(string fault, bool beforeText, AiFailure expected)
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        var text = JsonSerializer.Serialize(new InteractionAnalysisResult("private-answer"), AiContextBuilder.JsonOptions);
        var textEvent = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text }) + "\n\n";
        var contradiction = fault switch
        {
            "refusal-delta" => """{"type":"response.refusal.delta","delta":"private-refusal"}""",
            "refusal-done" => """{"type":"response.refusal.done","refusal":"private-refusal"}""",
            "item-added" => """{"type":"response.output_item.added","item":{"type":"function_call","arguments":"private-tool"}}""",
            "item-done" => """{"type":"response.output_item.done","item":{"type":"function_call","arguments":"private-tool"}}""",
            "part-refusal" => """{"type":"response.content_part.added","part":{"type":"refusal","refusal":"private-refusal"}}""",
            "part-tool" => """{"type":"response.content_part.done","part":{"type":"output_audio","audio":"private-tool"}}""",
            "tool-arguments" => """{"type":"response.function_call_arguments.delta","delta":"private-tool"}""",
            "tool-progress" => """{"type":"response.web_search_call.in_progress","item_id":"private-tool"}""",
            _ => null
        };
        var terminal = fault switch
        {
            "terminal-refusal" => """{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"private-refusal"}]}]}}""",
            "terminal-tool" => """{"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call","arguments":"private-tool"}]}}""",
            "terminal-content" => """{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","content":[{"type":"output_audio","audio":"private-tool"}]}]}}""",
            "terminal-direct-content" => """{"type":"response.completed","response":{"status":"completed","content":[{"type":"refusal","refusal":"private-refusal"}]}}""",
            _ => """{"type":"response.completed","response":{"status":"completed"}}"""
        };
        var contradictionEvent = contradiction is null ? string.Empty : "data: " + contradiction + "\n\n";
        app.Server.StreamBody = (beforeText ? contradictionEvent + textEvent : textEvent + contradictionEvent) + "data: " + terminal + "\n\n";
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var error = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateTextAsync(new AiTextRequest("Instructions", "Input")
        {
            StructuredOutput = AnalysisContract.OutputContract()
        }, Cancellation));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(1, app.Server.InferenceCount);
        Assert.DoesNotContain("private-", error.Message);
        Assert.DoesNotContain("private-", app.Logs.Text);
        Assert.True((await app.OwnedConnection.GetStatusAsync(Cancellation)).Connected);
    }

    [Fact]
    public async Task Text_with_harmless_metadata_reasoning_and_terminal_text_remains_valid_and_private()
    {
        using var app = new ChatGptTestApp();
        await ConnectAnalysisModelAsync(app);
        var text = JsonSerializer.Serialize(new InteractionAnalysisResult("private-answer"), AiContextBuilder.JsonOptions);
        app.Server.StreamBody =
            "data: " + JsonSerializer.Serialize(new { type = "response.created", response = new { status = "in_progress" } }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { type = "response.future_metadata", metadata = "private-metadata" }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { type = "response.reasoning_summary_text.delta", delta = "private-reasoning" }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { type = "response.output_item.added", item = new { type = "reasoning", summary = Array.Empty<object>() } }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { type = "response.content_part.added", part = new { type = "output_text", text = "" } }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { type = "response.output_item.done", item = new { type = "message", content = new[] { new { type = "output_text", text } } } }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { type = "response.completed", response = new { status = "completed", model = "returned-model", output = new object[]
            {
                new { type = "reasoning", summary = new[] { new { type = "summary_text", text = "private-reasoning" } } },
                new { type = "message", content = new[] { new { type = "output_text", text } } }
            } } }) + "\n\n";
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAiClient>().GenerateTextAsync(new AiTextRequest("Instructions", "Input")
        {
            StructuredOutput = AnalysisContract.OutputContract()
        }, Cancellation);
        Assert.Equal(text, result.Text);
        Assert.Equal("gpt-5.6-sol", result.RequestedModel);
        Assert.Equal("returned-model", result.ReturnedModel);
        Assert.DoesNotContain("private-reasoning", result.Text);
        Assert.DoesNotContain("private-", app.Logs.Text);
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
        var connection = disconnectingHost.OwnedConnection;
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
        await app.OwnedConnection.DisconnectAsync(Cancellation);
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
        Assert.True((await app.OwnedConnection.GetStatusAsync(Cancellation)).Connected);
    }

    private static async Task ConnectAnalysisModelAsync(ChatGptTestApp app)
    {
        app.Server.ModelsBody = """{"models":[{"slug":"gpt-5.6-sol","display_name":"Test model","visibility":"list"}]}""";
        await app.ConnectAsync();
        await app.SelectModelAsync("gpt-5.6-sol");
    }
}
