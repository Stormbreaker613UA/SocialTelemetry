using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Infrastructure.AI;

namespace SocialTelemetry.Tests.Infrastructure.AI;

public sealed class ChatGptCapabilityTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Unknown_unsupported_and_supported_remain_distinct_and_invalid_capability_is_never_supported()
    {
        var capabilities = new AiModelCapabilities(AiCapabilitySupport.Supported, AiCapabilitySupport.Unsupported);
        Assert.True(capabilities.Supports(AiCapability.Text));
        Assert.Equal(AiCapabilitySupport.Unsupported, capabilities.GetSupport(AiCapability.Vision));
        Assert.Equal(AiCapabilitySupport.Unknown, capabilities.GetSupport(AiCapability.StructuredOutput));
        Assert.False(capabilities.Supports(AiCapability.Vision));
        Assert.False(capabilities.Supports(AiCapability.StructuredOutput));
        Assert.False(capabilities.Supports((AiCapability)999));
        var selected = new AiSelectedModel("synthetic", "selected", capabilities);
        Assert.Equal(AiCapability.Text, Assert.Single(selected.Capabilities));
        var copy = new AiSelectedModel("synthetic", "selected", [AiCapability.Text]);
        Assert.Equal(AiCapabilitySupport.Unknown, copy.CapabilitySupport.Vision);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("whitespace")]
    [InlineData("control")]
    [InlineData("duplicate")]
    [InlineData("conflict")]
    [InlineData("invalid-enum")]
    [InlineData("null-profile")]
    [InlineData("null-list")]
    public void Invalid_compatibility_profiles_are_rejected(string failure)
    {
        var options = new ChatGptOptions();
        var profile = new ChatGptModelCompatibilityProfile { ModelId = "exact-model", Text = AiCapabilitySupport.Supported };
        options.CompatibilityProfiles.Add(profile);
        switch (failure)
        {
            case "empty": profile.ModelId = ""; break;
            case "blank": profile.ModelId = " "; break;
            case "whitespace": profile.ModelId = " exact-model"; break;
            case "control": profile.ModelId = "exact\0model"; break;
            case "duplicate": options.CompatibilityProfiles.Add(new() { ModelId = profile.ModelId, Text = profile.Text }); break;
            case "conflict": options.CompatibilityProfiles.Add(new() { ModelId = profile.ModelId, Text = AiCapabilitySupport.Unsupported }); break;
            case "invalid-enum": profile.Vision = (AiCapabilitySupport)999; break;
            case "null-profile": options.CompatibilityProfiles.Add(null!); break;
            case "null-list": options.CompatibilityProfiles = null!; break;
        }
        Assert.False(options.HasValidCompatibilityProfiles());
    }

    [Fact]
    public void Empty_profiles_are_valid_and_resolution_is_exact_case_sensitive_and_copies_validated_states()
    {
        var options = new ChatGptOptions();
        Assert.True(options.HasValidCompatibilityProfiles());
        var empty = new ChatGptCapabilityResolver(Options.Create(options));
        Assert.Empty(empty.Resolve("new-model").SupportedCapabilities);
        options.CompatibilityProfiles =
        [
            new() { ModelId = "model", Text = AiCapabilitySupport.Supported, Vision = AiCapabilitySupport.Unsupported },
            new() { ModelId = "MODEL", StructuredOutput = AiCapabilitySupport.Supported }
        ];
        Assert.True(options.HasValidCompatibilityProfiles());
        var resolver = new ChatGptCapabilityResolver(Options.Create(options));
        options.CompatibilityProfiles[0].Text = AiCapabilitySupport.Unsupported;
        Assert.True(resolver.Resolve("model").Supports(AiCapability.Text));
        Assert.Equal(AiCapabilitySupport.Unsupported, resolver.Resolve("model").Vision);
        Assert.True(resolver.Resolve("MODEL").Supports(AiCapability.StructuredOutput));
        Assert.Equal(new AiModelCapabilities(), resolver.Resolve("model-new"));
        Assert.Equal(new AiModelCapabilities(), resolver.Resolve("Model"));
    }

    [Theory]
    [InlineData(AiCapabilitySupport.Unknown)]
    [InlineData(AiCapabilitySupport.Unsupported)]
    [InlineData(AiCapabilitySupport.Supported)]
    public async Task Catalog_selected_model_and_generation_share_the_same_profile_without_client_override(AiCapabilitySupport support)
    {
        using var app = new ChatGptTestApp(configure: options => options.CompatibilityProfiles.Add(new()
            { ModelId = "provider-new", Text = support, Vision = support, StructuredOutput = support }));
        await app.ConnectAsync();
        app.Server.ModelsBody = """{"models":[{"slug":"provider-new","display_name":"New provider model","visibility":"list"},{"slug":"model-a","display_name":"Original","visibility":"list"},{"slug":"hidden","display_name":"Hidden","visibility":"hide"}]}""";
        await app.SelectModelAsync("provider-new");
        var catalog = await app.Client.GetFromJsonAsync<JsonElement>("/ai-connection/chatgpt/models", Cancellation);
        Assert.Equal(new[] { "provider-new", "model-a" }, catalog.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("id").GetString()));
        Assert.Equal("New provider model", catalog.GetProperty("models")[0].GetProperty("displayName").GetString());
        var states = catalog.GetProperty("models")[0].GetProperty("capabilitySupport");
        Assert.Equal(support.ToString(), states.GetProperty("text").GetString());
        Assert.Equal(support.ToString(), states.GetProperty("vision").GetString());
        Assert.Equal(support.ToString(), states.GetProperty("structuredOutput").GetString());
        Assert.Equal("provider-new", catalog.GetProperty("selectedModel").GetString());
        Assert.True(catalog.GetProperty("selectedModelAvailable").GetBoolean());
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var selected = await client.GetSelectedModelAsync(Cancellation);
        Assert.Equal("provider-new", selected.ModelId);
        Assert.Equal(support, selected.CapabilitySupport.Text);
        // A caller-supplied supported snapshot must not override the adapter's trusted state.
        var asserted = selected with { CapabilitySupport = new(AiCapabilitySupport.Supported, AiCapabilitySupport.Supported, AiCapabilitySupport.Supported) };
        var request = new AiTextRequest("Synthetic instructions", "Synthetic input")
        {
            SelectedModel = asserted,
            Images = [new(Guid.NewGuid(), "image/png", new byte[] { 1 })],
            StructuredOutput = new("synthetic", JsonSerializer.SerializeToElement(new { type = "object" }))
        };
        if (support == AiCapabilitySupport.Supported)
        {
            var result = await client.GenerateTextAsync(request, Cancellation);
            Assert.Equal("provider-new", result.RequestedModel);
            using var payload = JsonDocument.Parse(app.Server.LastInferenceBody!);
            Assert.Equal("provider-new", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(1, app.Server.InferenceCount);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateTextAsync(request, Cancellation));
            Assert.Equal(AiFailure.CapabilityMissing, error.Failure);
            Assert.Equal(0, app.Server.InferenceCount);
            Assert.Null(app.Server.LastInferenceBody);
        }
        Assert.DoesNotContain("Synthetic input", app.Logs.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Partial_new_ID_configuration_does_not_inherit_shipped_image_or_structured_assertions(bool image)
    {
        using var app = new ChatGptTestApp(configuration: new()
        {
            ["ChatGpt:CompatibilityProfiles:0:ModelId"] = "provider-partial",
            ["ChatGpt:CompatibilityProfiles:0:Text"] = "Supported"
        });
        await app.ConnectAsync();
        app.Server.ModelsBody = """{"models":[{"slug":"provider-partial","display_name":"Provider partial model","visibility":"list"}]}""";
        await app.SelectModelAsync("provider-partial");
        var catalog = await app.Client.GetFromJsonAsync<JsonElement>("/ai-connection/chatgpt/models", Cancellation);
        var states = catalog.GetProperty("models")[0].GetProperty("capabilitySupport");
        Assert.Equal("Supported", states.GetProperty("text").GetString());
        Assert.Equal("Unknown", states.GetProperty("vision").GetString());
        Assert.Equal("Unknown", states.GetProperty("structuredOutput").GetString());
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var selected = await client.GetSelectedModelAsync(Cancellation);
        Assert.Equal("provider-partial", selected.ModelId);
        Assert.True(selected.Supports(AiCapability.Text));
        Assert.False(selected.Supports(AiCapability.Vision));
        Assert.False(selected.Supports(AiCapability.StructuredOutput));
        var request = image
            ? new AiTextRequest("Synthetic instructions", "Synthetic input") { Images = [new(Guid.NewGuid(), "image/png", new byte[] { 1 })] }
            : new AiTextRequest("Synthetic instructions", "Synthetic input") { StructuredOutput = new("synthetic", JsonSerializer.SerializeToElement(new { type = "object" })) };
        var error = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateTextAsync(request, Cancellation));
        Assert.Equal(AiFailure.CapabilityMissing, error.Failure);
        Assert.Equal(0, app.Server.InferenceCount);
        Assert.Null(app.Server.LastInferenceBody);
    }
}
