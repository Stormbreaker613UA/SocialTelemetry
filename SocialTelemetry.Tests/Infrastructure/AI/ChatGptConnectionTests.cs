using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Tests.Infrastructure.AI;

public sealed class ChatGptConnectionTests
{
    [Fact]
    public async Task Connection_uses_documented_PKCE_contract_and_protects_credentials()
    {
        using var app = new ChatGptTestApp();
        var disconnected = await app.Client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status");
        Assert.NotNull(disconnected);
        Assert.False(disconnected.Connected);
        var status = await app.ConnectAsync();
        Assert.True(status.Connected);
        Assert.True(status.InferencePermissionAvailable);
        Assert.Null(status.SelectedModel);
        Assert.NotNull(app.Browser.LastUri);
        Assert.Equal("https://auth.openai.com/api/accounts/authorize", app.Browser.LastUri.GetLeftPart(UriPartial.Path));
        var authorization = QueryHelpers.ParseQuery(app.Browser.LastUri.Query);
        Assert.Equal("dynamic_agent_client", authorization["client_id"].ToString());
        Assert.Equal("SocialTelemetry", authorization["agent_name_hint"].ToString());
        Assert.StartsWith("urn:uuid:", authorization["ext_agent_host_id"].ToString());
        Assert.Equal("http://127.0.0.1:5059" + ChatGptOptions.CallbackPath, authorization["redirect_uri"].ToString());
        Assert.Equal("S256", authorization["code_challenge_method"].ToString());
        Assert.Equal("https://api.openai.com/v1", authorization["resource"].ToString());
        Assert.Contains("chatgpt.tokens.use.direct", authorization["scope"].ToString());
        var exchange = Assert.Single(app.Server.TokenRequests);
        Assert.Equal("oaiapp_test", exchange["client_id"]);
        Assert.Equal(authorization["redirect_uri"].ToString(), exchange["redirect_uri"]);
        Assert.Equal(authorization["code_challenge"].ToString(),
            WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(exchange["code_verifier"]))));
        Assert.False(exchange.ContainsKey("client_secret"));
        var protectedBytes = await File.ReadAllBytesAsync(Path.Combine(app.DataDirectory, "chatgpt-state.bin"));
        Assert.DoesNotContain("private-access-", Encoding.UTF8.GetString(protectedBytes));
        var protector = CreateProtector(app);
        var stored = Encoding.UTF8.GetString(protector.Unprotect(protectedBytes));
        Assert.Contains("private-access-1", stored);
        Assert.Contains("private-refresh-1", stored);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(status));
        Assert.DoesNotContain("private-", app.Logs.Text);
        Assert.DoesNotContain(exchange["code_verifier"], app.Logs.Text);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("missing-state")]
    [InlineData("expired")]
    [InlineData("denied")]
    [InlineData("missing-client")]
    [InlineData("dynamic-client")]
    [InlineData("duplicate-state")]
    public async Task Invalid_callback_never_exchanges_a_code(string fault)
    {
        using var app = new ChatGptTestApp();
        await app.BeginAsync();
        if (fault == "expired") app.Clock.Advance(TimeSpan.FromMinutes(11));
        var url = fault switch
        {
            "state" => app.CallbackUrl(state: "wrong-state"),
            "missing-state" => ChatGptOptions.CallbackPath + "?code=private-code&client_id=oaiapp_test",
            "denied" => app.CallbackUrl(error: "access_denied") + "&error_description=private-provider-detail",
            "missing-client" => app.CallbackUrl(clientId: null),
            "dynamic-client" => app.CallbackUrl(clientId: "dynamic_agent_client"),
            "duplicate-state" => app.CallbackUrl() + "&state=extra",
            _ => app.CallbackUrl()
        };
        using var response = await app.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("private-", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, app.Server.ExchangeCount);
        Assert.DoesNotContain("private-", app.Logs.Text);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("nonce")]
    public async Task Invalid_ID_token_is_rejected(string fault)
    {
        using var app = new ChatGptTestApp();
        app.Server.IdentityFault = fault;
        await app.BeginAsync();
        using var response = await app.Client.GetAsync(app.CallbackUrl());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("InvalidIdentity", await response.Content.ReadAsStringAsync());
        var status = await app.Client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status");
        Assert.NotNull(status);
        Assert.False(status.Connected);
        Assert.DoesNotContain("private-", app.Logs.Text);
    }

    [Fact]
    public async Task Callback_is_single_use_and_returning_account_identity_cannot_be_replaced()
    {
        using var app = new ChatGptTestApp();
        var initial = await app.ConnectAsync();
        using var replay = await app.Client.GetAsync(app.CallbackUrl());
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(1, app.Server.ExchangeCount);
        await app.BeginAsync();
        using var changedClient = await app.Client.GetAsync(app.CallbackUrl(clientId: "oaiapp_other"));
        Assert.Equal(HttpStatusCode.BadRequest, changedClient.StatusCode);
        Assert.Equal(1, app.Server.ExchangeCount);
        await app.BeginAsync();
        app.Server.Subject = "different-private-subject";
        using var changedSubject = await app.Client.GetAsync(app.CallbackUrl(clientId: null));
        Assert.Equal(HttpStatusCode.BadGateway, changedSubject.StatusCode);
        var preserved = await app.Client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status");
        Assert.NotNull(preserved);
        Assert.Equal(initial.ConnectionId, preserved.ConnectionId);
        Assert.True(preserved.Connected);
    }

    [Fact]
    public async Task Identity_without_inference_permission_stays_connected_but_cannot_use_models()
    {
        using var app = new ChatGptTestApp();
        app.Server.Scope = "openid profile email";
        var status = await app.ConnectAsync();
        Assert.True(status.Connected);
        Assert.False(status.InferencePermissionAvailable);
        using var response = await app.Client.GetAsync("/ai-connection/chatgpt/models");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await app.BeginAsync(new { RequestConsent = true });
        Assert.NotNull(app.Browser.LastUri);
        var parameters = QueryHelpers.ParseQuery(app.Browser.LastUri.Query);
        Assert.Equal("consent", parameters["prompt"].ToString());
        Assert.Equal("oaiapp_test", parameters["client_id"].ToString());
        Assert.False(parameters.ContainsKey("agent_name_hint"));
    }

    [Fact]
    public async Task Code_exchange_failure_retains_registration_for_retry()
    {
        using var app = new ChatGptTestApp();
        app.Server.TokenError = "invalid_grant";
        await app.BeginAsync();
        using var callback = await app.Client.GetAsync(app.CallbackUrl());
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        var status = await app.Client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status");
        Assert.NotNull(status);
        var savedAccount = Assert.Single(status.Accounts);
        Assert.False(savedAccount.Connected);
        await app.BeginAsync(new { ConnectionId = savedAccount.Id });
        Assert.NotNull(app.Browser.LastUri);
        Assert.Equal("oaiapp_test", QueryHelpers.ParseQuery(app.Browser.LastUri.Query)["client_id"].ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disconnect_clears_tokens_preserves_registration_and_reports_revocation(bool providerAvailable)
    {
        using var app = new ChatGptTestApp();
        var connected = await app.ConnectAsync();
        Assert.NotNull(app.Browser.LastUri);
        var hostId = QueryHelpers.ParseQuery(app.Browser.LastUri.Query)["ext_agent_host_id"].ToString();
        app.Server.RevokeStatus = providerAvailable ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
        using var response = await app.Client.DeleteAsync("/ai-connection/chatgpt");
        response.EnsureSuccessStatusCode();
        var disconnected = await response.Content.ReadFromJsonAsync<ChatGptDisconnectResult>();
        Assert.NotNull(disconnected);
        Assert.Equal(providerAvailable, disconnected.RemoteRevocationConfirmed);
        Assert.Equal(providerAvailable ? 1 : 2, app.Server.RevokeCount);
        var stored = Encoding.UTF8.GetString(CreateProtector(app).Unprotect(await File.ReadAllBytesAsync(Path.Combine(app.DataDirectory, "chatgpt-state.bin"))));
        Assert.DoesNotContain("private-access-", stored);
        Assert.DoesNotContain("private-refresh-", stored);
        Assert.Contains("oaiapp_test", stored);
        using var restarted = new ChatGptTestApp(app);
        var status = await restarted.Client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status");
        Assert.NotNull(status);
        Assert.False(status.Connected);
        Assert.Equal(connected.ConnectionId, status.ConnectionId);
        await restarted.BeginAsync();
        Assert.NotNull(restarted.Browser.LastUri);
        var parameters = QueryHelpers.ParseQuery(restarted.Browser.LastUri.Query);
        Assert.Equal(hostId, parameters["ext_agent_host_id"].ToString());
        Assert.Equal("oaiapp_test", parameters["client_id"].ToString());
        Assert.False(parameters.ContainsKey("id_token_hint"));
    }

    [Fact]
    public async Task Concurrent_refresh_across_two_hosts_uses_the_rotating_token_once()
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        app.Clock.Advance(TimeSpan.FromHours(2));
        app.Server.RefreshDelay = TimeSpan.FromMilliseconds(100);
        using var secondHost = new ChatGptTestApp(app);
        var requests = Enumerable.Range(0, 8).Select(index =>
            (index % 2 == 0 ? app.Client : secondHost.Client).GetAsync("/ai-connection/chatgpt/models"));
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses)
        {
            using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, app.Server.RefreshCount);
        Assert.Equal("private-access-2", app.Server.LastBearer);
        var refresh = app.Server.TokenRequests.Last();
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("oaiapp_test", refresh["client_id"]);
        Assert.Equal("https://api.openai.com/v1", refresh["resource"]);
        Assert.False(refresh.ContainsKey("scope"));
    }

    [Fact]
    public async Task Caller_cancellation_during_refresh_still_persists_replacement_credentials()
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        app.Clock.Advance(TimeSpan.FromHours(2));
        using var cancellation = new CancellationTokenSource();
        app.Server.CancelDuringRefresh = cancellation;
        var connection = app.Services.GetRequiredService<ChatGptConnection>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetModelsAsync(cancellation.Token));
        await connection.GetModelsAsync(CancellationToken.None);
        Assert.Equal(1, app.Server.RefreshCount);
        Assert.Equal("private-access-2", app.Server.LastBearer);
    }

    [Theory]
    [InlineData("invalid_grant", false)]
    [InlineData("refresh_token_reused", false)]
    [InlineData("invalid_client", false)]
    [InlineData("temporarily_unavailable", true)]
    public async Task Refresh_errors_distinguish_unusable_credentials_from_temporary_failures(string error, bool temporary)
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        app.Clock.Advance(TimeSpan.FromHours(2));
        app.Server.TokenError = error;
        app.Server.TokenErrorStatus = temporary ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest;
        using var response = await app.Client.GetAsync("/ai-connection/chatgpt/models");
        Assert.False(response.IsSuccessStatusCode);
        Assert.DoesNotContain("private-", await response.Content.ReadAsStringAsync());
        var status = await app.Client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status");
        Assert.NotNull(status);
        Assert.Equal(temporary, status.Connected);
        Assert.DoesNotContain("private-", app.Logs.Text);
    }

    [Fact]
    public async Task Models_use_live_catalog_and_do_not_silently_replace_an_unavailable_selection()
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        var models = await app.Client.GetFromJsonAsync<ChatGptModelCatalog>("/ai-connection/chatgpt/models");
        Assert.NotNull(models);
        Assert.Equal("model-a", Assert.Single(models.Models).Id);
        await app.SelectModelAsync();
        app.Server.ModelsBody = "{\"models\":[]}";
        var unavailable = await app.Client.GetFromJsonAsync<ChatGptModelCatalog>("/ai-connection/chatgpt/models");
        Assert.NotNull(unavailable);
        Assert.Empty(unavailable.Models);
        Assert.Equal("model-a", unavailable.SelectedModel);
        Assert.False(unavailable.SelectedModelAvailable);
        await using var scope = app.Services.CreateAsyncScope();
        var aiClient = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var error = await Assert.ThrowsAsync<AiProviderException>(() => aiClient.GenerateTextAsync(new("Instructions", "private-input"), CancellationToken.None));
        Assert.Equal(AiFailure.ModelUnavailable, error.Failure);
        Assert.Equal(0, app.Server.InferenceCount);
    }

    [Theory]
    [InlineData(503, "{\"detail\":\"private-provider-detail\"}", 503)]
    [InlineData(429, "{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"private-provider-detail\"}}", 429)]
    [InlineData(403, "{\"error\":{\"code\":\"subscription_sharing_user_not_eligible\",\"message\":\"private-provider-detail\"}}", 502)]
    [InlineData(200, "private-invalid-json", 502)]
    [InlineData(200, "{\"data\":[]}", 502)]
    public async Task Provider_failures_are_safe_and_do_not_clear_credentials(int providerStatus, string body, int expectedStatus)
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        app.Server.ModelsStatus = (HttpStatusCode)providerStatus;
        app.Server.ModelsBody = body;
        using var response = await app.Client.GetAsync("/ai-connection/chatgpt/models");
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.DoesNotContain("private-", await response.Content.ReadAsStringAsync());
        var status = await app.Client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status");
        Assert.NotNull(status);
        Assert.True(status.Connected);
        Assert.DoesNotContain("private-", app.Logs.Text);
    }

    [Theory]
    [InlineData("missing-header")]
    [InlineData("origin")]
    [InlineData("remote")]
    [InlineData("host")]
    public async Task Connection_endpoints_reject_nonlocal_or_cross_site_requests(string fault)
    {
        using var app = new ChatGptTestApp();
        if (fault == "missing-header") app.Client.DefaultRequestHeaders.Remove("X-SocialTelemetry-Local");
        if (fault == "origin") app.Client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        if (fault == "remote") app.RemoteAddress = IPAddress.Parse("192.0.2.1");
        if (fault == "host") app.Client.DefaultRequestHeaders.Host = "untrusted.example";
        using var response = await app.Client.PostAsJsonAsync("/ai-connection/chatgpt/connect", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(app.Browser.LastUri);
    }

    private static IDataProtector CreateProtector(ChatGptTestApp app) =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(app.DataDirectory, "chatgpt-keys")),
            builder => builder.SetApplicationName("SocialTelemetry.ChatGpt")).CreateProtector("OAuthCredentials.v1");
}
