using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Tests.Infrastructure.AI;

public sealed class ChatGptPlanAiClientTests
{
    [Fact]
    public async Task Text_transport_uses_nonstored_streaming_Responses_and_returns_only_completed_output()
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        await app.SelectModelAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();

        var result = await client.GenerateTextAsync(new AiTextRequest("private-instructions", "private-input"), CancellationToken.None);

        Assert.Equal("private-output", result.Text);
        Assert.Equal("chatgpt-plan", result.ProviderId);
        Assert.Equal("model-a", result.RequestedModel);
        Assert.Null(result.ReturnedModel);
        Assert.Equal("model-a", result.Model);
        Assert.NotNull(app.Server.LastInferenceBody);
        using var body = JsonDocument.Parse(app.Server.LastInferenceBody);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("model-a", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("private-instructions", body.RootElement.GetProperty("instructions").GetString());
        var input = Assert.Single(body.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("user", input.GetProperty("role").GetString());
        Assert.Equal("private-input", input.GetProperty("content").GetString());
        foreach (var forbidden in new[] { "previous_response_id", "conversation", "background", "max_output_tokens", "temperature", "metadata" })
            Assert.False(body.RootElement.TryGetProperty(forbidden, out _));
        Assert.Equal("private-access-1", app.Server.LastBearer);
        Assert.DoesNotContain("private-", app.Logs.Text);
    }

    [Fact]
    public async Task Selected_model_exposes_only_confirmed_neutral_capabilities()
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        await app.SelectModelAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();

        var selected = await client.GetSelectedModelAsync(CancellationToken.None);

        Assert.Equal("chatgpt-plan", selected.ProviderId);
        Assert.Equal("model-a", selected.ModelId);
        Assert.Equal(AiCapability.Text, Assert.Single(selected.Capabilities));
        Assert.True(selected.Supports(AiCapability.Text));
        Assert.False(selected.Supports(AiCapability.Vision));
        Assert.False(selected.Supports(AiCapability.StructuredOutput));
        Assert.False(selected.Supports((AiCapability)999));
        Assert.Equal(0, app.Server.InferenceCount);
    }

    [Fact]
    public async Task Completed_inference_preserves_requested_and_returned_model_provenance()
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        await app.SelectModelAsync();
        app.Server.StreamBody = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"private-output\"}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"model\":\"model-returned\"}}\n\n";
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();

        var result = await client.GenerateTextAsync(new AiTextRequest("Instructions", "Input"), CancellationToken.None);

        Assert.Equal("chatgpt-plan", result.ProviderId);
        Assert.Equal("model-a", result.RequestedModel);
        Assert.Equal("model-returned", result.ReturnedModel);
        Assert.Equal("model-returned", result.Model);
        Assert.Equal("private-output", result.Text);
    }

    [Theory]
    [InlineData("interrupted", AiFailure.IncompleteResponse)]
    [InlineData("incomplete", AiFailure.IncompleteResponse)]
    [InlineData("limit", AiFailure.UsageLimitReached)]
    [InlineData("unavailable", AiFailure.ProviderUnavailable)]
    [InlineData("malformed", AiFailure.MalformedResponse)]
    [InlineData("error", AiFailure.ProviderRejected)]
    public async Task Failed_streams_never_return_partial_output(string failure, AiFailure expected)
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        await app.SelectModelAsync();
        var terminalEvent = failure switch
        {
            "incomplete" => "{\"type\":\"response.incomplete\"}",
            "limit" => "{\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"private-provider-detail\"}}}",
            "unavailable" => "{\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_unavailable\"}}}",
            "malformed" => "private-invalid-json",
            "error" => "{\"type\":\"error\",\"code\":\"unknown\",\"message\":\"private-provider-detail\"}",
            _ => "[DONE]"
        };
        app.Server.StreamBody = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"private-partial-output\"}\n\ndata: " + terminalEvent + "\n\n";
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();

        var error = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateTextAsync(new("Instructions", "Input"), CancellationToken.None));

        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("private-", error.Message);
        Assert.DoesNotContain("private-", app.Logs.Text);
    }

    [Fact]
    public async Task Cancellation_remains_cancellation_and_preserves_credentials()
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        app.Server.CancelModels = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var connection = app.Services.GetRequiredService<ChatGptConnection>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetModelsAsync(cancellation.Token));

        Assert.True((await connection.GetStatusAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task Missing_connection_or_model_stops_before_inference()
    {
        using var app = new ChatGptTestApp();
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var notConnected = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateTextAsync(new("Instructions", "Input"), CancellationToken.None));
        Assert.Equal(AiFailure.NotConnected, notConnected.Failure);
        await app.ConnectAsync();
        var noModel = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateTextAsync(new("Instructions", "Input"), CancellationToken.None));
        Assert.Equal(AiFailure.ModelNotSelected, noModel.Failure);
        Assert.Equal(0, app.Server.InferenceCount);
    }

    [Fact]
    public async Task Damaged_credential_store_fails_safely_without_resetting_it()
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        var path = Path.Combine(app.DataDirectory, "chatgpt-state.bin");
        await File.WriteAllTextAsync(path, "private-corrupted-store", TestContext.Current.CancellationToken);

        using var response = await app.Client.GetAsync("/ai-connection/chatgpt/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("StorageUnavailable", body);
        Assert.DoesNotContain("private-", body);
        Assert.Equal("private-corrupted-store", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private-", app.Logs.Text);
    }

    [Fact]
    public async Task New_account_stays_separate_until_validated_and_has_its_own_model_selection()
    {
        using var app = new ChatGptTestApp();
        var original = await app.ConnectAsync();
        await app.SelectModelAsync();
        await app.BeginAsync(new { NewAccount = true });
        var whilePending = await app.Client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status", TestContext.Current.CancellationToken);
        Assert.NotNull(whilePending);
        Assert.Equal(original.ConnectionId, whilePending.ConnectionId);
        Assert.Equal("model-a", whilePending.SelectedModel);
        app.Server.Subject = "another-private-subject";

        using var response = await app.Client.GetAsync(app.CallbackUrl(clientId: "oaiapp_second"), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var switched = await response.Content.ReadFromJsonAsync<ChatGptConnectionStatus>(TestContext.Current.CancellationToken);
        Assert.NotNull(switched);
        Assert.NotEqual(original.ConnectionId, switched.ConnectionId);
        Assert.Equal(2, switched.Accounts.Count);
        Assert.Null(switched.SelectedModel);
        Assert.DoesNotContain("private-", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
