using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Serilog.Core;
using Serilog.Events;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Tests.Infrastructure.AI;

internal sealed class ChatGptTestApp : IDisposable
{
    private readonly WebApplicationFactory<Program> application;
    private readonly bool ownsDirectory;
    public string DataDirectory { get; }
    public TestBrowser Browser { get; } = new();
    public TestClock Clock { get; }
    public FakeChatGptServer Server { get; }
    public CapturedAiLogs Logs { get; } = new();
    public HttpClient Client { get; }
    public IPAddress RemoteAddress { get; set; } = IPAddress.Loopback;
    public IServiceProvider Services => application.Services;

    public ChatGptTestApp(ChatGptTestApp? sharedStorage = null, Action<ChatGptOptions>? configure = null)
    {
        ownsDirectory = sharedStorage is null;
        DataDirectory = sharedStorage?.DataDirectory ?? Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", "ChatGpt", Guid.NewGuid().ToString("N"));
        Clock = sharedStorage?.Clock ?? new TestClock();
        Server = sharedStorage?.Server ?? new FakeChatGptServer(Browser);
        application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", "Host=localhost;Database=unused");
            builder.ConfigureServices(services =>
            {
                var reconciliation = services.Single(service => service.ServiceType == typeof(IHostedService) &&
                    service.ImplementationType == typeof(AttachmentReconciliationService));
                services.Remove(reconciliation);
                services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) &&
                    service.ImplementationType == typeof(MediaDirectoryBinding)));
                services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) &&
                    service.ImplementationType == typeof(ProfileAvatarReconciliationService)));
                services.Configure<ChatGptOptions>(options => options.DataDirectory = DataDirectory);
                if (configure is not null) services.Configure(configure);
                services.RemoveAll<ISystemBrowser>();
                services.AddSingleton<ISystemBrowser>(Browser);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.AddHttpClient<ChatGptHttpClient>().ConfigurePrimaryHttpMessageHandler(() => Server).RemoveAllLoggers();
                services.AddSingleton<ILogEventSink>(Logs);
                services.AddSingleton<IStartupFilter>(new LocalAddressFilter(this));
            });
        });
        Client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5059"),
            AllowAutoRedirect = false
        });
        Client.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1");
    }

    public async Task BeginAsync(object? request = null)
    {
        using var response = await Client.PostAsJsonAsync("/ai-connection/chatgpt/connect", request ?? new { });
        response.EnsureSuccessStatusCode();
        Assert.NotNull(Browser.LastUri);
    }

    public string CallbackUrl(string? state = null, string? clientId = "oaiapp_test", string? error = null)
    {
        Assert.NotNull(Browser.LastUri);
        var query = QueryHelpers.ParseQuery(Browser.LastUri.Query);
        return QueryHelpers.AddQueryString(ChatGptOptions.CallbackPath, new Dictionary<string, string?>
        {
            ["state"] = state ?? query["state"].ToString(),
            ["code"] = error is null ? "private-authorization-code" : null,
            ["client_id"] = clientId,
            ["error"] = error
        });
    }

    public async Task<ChatGptConnectionStatus> ConnectAsync()
    {
        await BeginAsync();
        using var response = await Client.GetAsync(CallbackUrl());
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<ChatGptConnectionStatus>();
        Assert.NotNull(status);
        return status;
    }

    public async Task SelectModelAsync(string modelId = "model-a")
    {
        using var response = await Client.PutAsJsonAsync("/ai-connection/chatgpt/model", new { ModelId = modelId });
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        Client.Dispose();
        application.Dispose();
        if (ownsDirectory)
        {
            Server.DisposeKey();
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
        }
    }

    private sealed class LocalAddressFilter(ChatGptTestApp app) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = app.RemoteAddress;
                await nextMiddleware();
            });
            next(builder);
        };
    }
}

internal sealed class TestBrowser : ISystemBrowser
{
    public Uri? LastUri { get; private set; }
    public void Open(Uri authorizationUri) => LastUri = authorizationUri;
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

internal sealed class CapturedAiLogs : ILogEventSink
{
    public ConcurrentQueue<LogEvent> Events { get; } = new();
    public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    public string Text => string.Join('\n', Events.Select(logEvent => $"{logEvent.RenderMessage()} {logEvent.Exception} {string.Join(' ', logEvent.Properties)}"));
}

internal sealed class FakeChatGptServer(TestBrowser browser) : HttpMessageHandler
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly Dictionary<string, string> refreshTokens = [];
    private int tokenNumber;
    public int RefreshCount { get; private set; }
    public int ExchangeCount { get; private set; }
    public int RevokeCount { get; private set; }
    public int InferenceCount { get; private set; }
    public string Subject { get; set; } = "private-subject";
    public string Scope { get; set; } = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public string? IdentityFault { get; set; }
    public bool IncludeIdTokenOnRefresh { get; set; }
    public HttpStatusCode DiscoveryStatus { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode JwksStatus { get; set; } = HttpStatusCode.OK;
    public bool CancelJwks { get; set; }
    public string? TokenError { get; set; }
    public HttpStatusCode TokenErrorStatus { get; set; } = HttpStatusCode.BadRequest;
    public HttpStatusCode ModelsStatus { get; set; } = HttpStatusCode.OK;
    public string ModelsBody { get; set; } = """{"models":[{"slug":"model-a","display_name":"Model A","visibility":"list"},{"slug":"hidden","display_name":"Hidden","visibility":"hide"}]}""";
    public string StreamBody { get; set; } = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"private-output\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n";
    public TaskCompletionSource? InferenceStarted { get; set; }
    public TaskCompletionSource? ContinueInference { get; set; }
    public bool IgnoreInferenceCancellation { get; set; }
    public string? InferenceMediaType { get; set; } = "text/event-stream";
    public HttpStatusCode RevokeStatus { get; set; } = HttpStatusCode.OK;
    public TimeSpan RefreshDelay { get; set; }
    public CancellationTokenSource? CancelDuringRefresh { get; set; }
    public bool CancelModels { get; set; }
    public ConcurrentQueue<Dictionary<string, string>> TokenRequests { get; } = new();
    public string? LastInferenceBody { get; private set; }
    public string? LastBearer { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing test URI.");
        LastBearer = request.Headers.Authorization?.Parameter;
        if (uri.AbsoluteUri == "https://auth.openai.com/.well-known/openid-configuration")
        {
            if (DiscoveryStatus != HttpStatusCode.OK) return Json(new { error = "private-provider-detail" }, DiscoveryStatus);
            return Json(new
            {
                issuer = "https://auth.openai.com",
                authorization_endpoint = "https://auth.openai.com/api/accounts/authorize",
                token_endpoint = "https://auth.openai.com/api/accounts/oauth/token",
                jwks_uri = "https://auth.openai.com/.well-known/jwks.json",
                revocation_endpoint = "https://auth.openai.com/test/revoke"
            });
        }
        if (uri.AbsoluteUri == "https://auth.openai.com/.well-known/jwks.json")
        {
            if (CancelJwks) throw new OperationCanceledException(cancellationToken);
            if (JwksStatus != HttpStatusCode.OK) return Json(new { error = "private-provider-detail" }, JwksStatus);
            var parameters = rsa.ExportParameters(false);
            return Json(new { keys = new[] { new { kty = "RSA", kid = "test-key", use = "sig", alg = "RS256",
                n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } } });
        }
        if (uri.AbsoluteUri == "https://auth.openai.com/api/accounts/oauth/token")
        {
            Assert.NotNull(request.Content);
            var fields = QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(cancellationToken))
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            TokenRequests.Enqueue(fields);
            var refreshing = fields["grant_type"] == "refresh_token";
            if (refreshing)
            {
                RefreshCount++;
                CancelDuringRefresh?.Cancel();
                await Task.Delay(RefreshDelay, cancellationToken);
            }
            else ExchangeCount++;
            if (TokenError is not null) return Json(new { error = TokenError, error_description = "private-provider-detail" }, TokenErrorStatus);
            var clientId = fields["client_id"];
            if (refreshing && (!refreshTokens.TryGetValue(clientId, out var expected) || fields["refresh_token"] != expected))
                return Json(new { error = "refresh_token_reused" }, HttpStatusCode.BadRequest);
            tokenNumber++;
            var refreshToken = "private-refresh-" + tokenNumber;
            refreshTokens[clientId] = refreshToken;
            var nonce = browser.LastUri is null ? "missing" : QueryHelpers.ParseQuery(browser.LastUri.Query)["nonce"].ToString();
            return Json(new
            {
                access_token = "private-access-" + tokenNumber,
                refresh_token = refreshToken,
                id_token = !refreshing || IncludeIdTokenOnRefresh ? CreateIdToken(clientId, nonce) : null,
                token_type = "Bearer",
                expires_in = 3600,
                scope = Scope
            });
        }
        if (uri.AbsoluteUri == "https://auth.openai.com/test/revoke")
        {
            RevokeCount++;
            Assert.NotNull(request.Content);
            var form = QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal("refresh_token", form["token_type_hint"].ToString());
            Assert.StartsWith("private-refresh-", form["token"].ToString());
            return new HttpResponseMessage(RevokeStatus) { Content = new StringContent(string.Empty) };
        }
        if (uri.AbsoluteUri == "https://api.openai.com/v1/models")
        {
            if (CancelModels) await Task.Delay(Timeout.Infinite, cancellationToken);
            Assert.StartsWith("private-access-", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(ModelsStatus) { Content = new StringContent(ModelsBody, Encoding.UTF8, "application/json") };
        }
        if (uri.AbsoluteUri == "https://api.openai.com/v1/responses")
        {
            InferenceCount++;
            InferenceStarted?.TrySetResult();
            if (ContinueInference is { } completion)
                await completion.Task.WaitAsync(IgnoreInferenceCancellation ? CancellationToken.None : cancellationToken);
            Assert.NotNull(request.Content);
            LastInferenceBody = await request.Content.ReadAsStringAsync(cancellationToken);
            var content = new StringContent(StreamBody, Encoding.UTF8);
            content.Headers.ContentType = InferenceMediaType is null ? null : new(InferenceMediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        throw new InvalidOperationException("Unexpected provider endpoint in fake HTTP handler.");
    }

    private string CreateIdToken(string clientId, string nonce)
    {
        using var wrongKey = RSA.Create(2048);
        var key = new RsaSecurityKey(IdentityFault == "signature" ? wrongKey : rsa) { KeyId = "test-key" };
        var expires = IdentityFault == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddHours(1);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = IdentityFault == "issuer" ? "https://invalid.example" : "https://auth.openai.com",
            Audience = IdentityFault == "audience" ? "wrong-client" : clientId,
            NotBefore = DateTime.UtcNow.AddHours(-1),
            IssuedAt = DateTime.UtcNow.AddHours(-1),
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Subject,
                ["nonce"] = IdentityFault == "nonce" ? "wrong-nonce" : nonce
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    public void DisposeKey() => rsa.Dispose();
}
