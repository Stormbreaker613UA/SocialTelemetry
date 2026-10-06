using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ChatGptHttpClient(HttpClient httpClient, TimeProvider timeProvider, ILogger<ChatGptHttpClient> logger)
{
    private JsonElement? discovery;
    private ICollection<SecurityKey>? signingKeys;
    private DateTimeOffset keysExpireAt;

    internal async Task<ChatGptTokens> ExchangeAsync(Dictionary<string, string> fields, bool refresh, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatGptProtocol.Token)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var code = ErrorCode(body);
            if (refresh && code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or
                "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused")
                throw new AiProviderException(AiFailure.ReconnectRequired);
            if (code == "invalid_client") throw new AiProviderException(AiFailure.InvalidClient);
            if (!refresh && code == "invalid_grant") throw new AiProviderException(AiFailure.InvalidCallback);
            ThrowProviderError(response.StatusCode, code);
        }

        if (RequiredString(body, "token_type") != "Bearer" ||
            !body.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number ||
            !expires.TryGetInt32(out var seconds) || seconds <= 0)
            throw new AiProviderException(AiFailure.MalformedResponse);

        var tokens = new ChatGptTokens
        {
            AccessToken = RequiredString(body, "access_token"),
            RefreshToken = OptionalString(body, "refresh_token"),
            IdToken = OptionalString(body, "id_token"),
            Scopes = RequiredString(body, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ExpiresAt = timeProvider.GetUtcNow().AddSeconds(seconds)
        };
        if (refresh && string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new AiProviderException(AiFailure.MalformedResponse);
        return tokens;
    }

    internal async Task<string> ValidateIdentityAsync(string? idToken, string clientId, string? nonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) throw new AiProviderException(AiFailure.InvalidIdentity);
        var configuration = await GetDiscoveryAsync(cancellationToken);
        if (signingKeys is null || keysExpireAt <= timeProvider.GetUtcNow())
            await RefreshKeysAsync(configuration, cancellationToken);

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = ChatGptProtocol.Issuer,
            ValidAudience = clientId,
            ValidateIssuer = true,
            ValidateAudience = true,
            RequireAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ClockSkew = TimeSpan.FromSeconds(5)
        };
        var result = await handler.ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            await RefreshKeysAsync(configuration, cancellationToken);
            parameters.IssuerSigningKeys = signingKeys;
            result = await handler.ValidateTokenAsync(idToken, parameters);
        }
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token ||
            string.IsNullOrWhiteSpace(token.Subject) || !token.TryGetPayloadValue<long>("iat", out _) ||
            (nonce is not null && (!token.TryGetPayloadValue<string>("nonce", out var returnedNonce) || !FixedEquals(nonce, returnedNonce))))
            throw new AiProviderException(AiFailure.InvalidIdentity);
        var hasAuthorizedParty = token.TryGetPayloadValue<string>("azp", out var authorizedParty);
        if ((hasAuthorizedParty && authorizedParty != clientId) || (token.Audiences.Count() > 1 && !hasAuthorizedParty))
            throw new AiProviderException(AiFailure.InvalidIdentity);
        return token.Subject;
    }

    internal async Task RevokeAsync(ChatGptAccount account, CancellationToken cancellationToken)
    {
        var configuration = await GetDiscoveryAsync(cancellationToken);
        var endpoint = TrustedAuthEndpoint(configuration, "revocation_endpoint");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = account.Tokens?.RefreshToken ?? string.Empty,
                ["token_type_hint"] = "refresh_token",
                ["client_id"] = account.ClientId
            })
        };
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK) ThrowProviderError(response.StatusCode, null);
    }

    internal async Task<IReadOnlyList<ChatGptModel>> GetModelsAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, ChatGptProtocol.Resource + "/models", accessToken);
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode) ThrowProviderError(response.StatusCode, ErrorCode(body));
        if (!body.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new AiProviderException(AiFailure.MalformedResponse);
        var result = new List<ChatGptModel>();
        foreach (var model in models.EnumerateArray())
        {
            if (OptionalString(model, "visibility") == "list")
                result.Add(new ChatGptModel(RequiredString(model, "slug"), RequiredString(model, "display_name")));
        }
        return result;
    }

    internal async Task<(string Text, string? ReturnedModel)> GenerateAsync(
        string accessToken, string model, AiTextRequest input, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var request = AuthorizedRequest(HttpMethod.Post, ChatGptProtocol.Resource + "/responses", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            object content = input.Input;
            if (input.Images.Count > 0)
            {
                var parts = new List<object> { new { type = "input_text", text = input.Input } };
                foreach (var image in input.Images)
                {
                    parts.Add(new { type = "input_text", text = "Evidence ID: " + image.EvidenceId });
                    parts.Add(new { type = "input_image", image_url = "data:" + image.MimeType + ";base64," + Convert.ToBase64String(image.Bytes.Span), detail = "auto" });
                }
                content = parts;
            }
            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["instructions"] = input.Instructions,
                ["input"] = new[] { new { role = "user", content } },
                ["store"] = false,
                ["stream"] = true
            };
            if (input.StructuredOutput is { } outputContract)
                payload["text"] = new { format = new { type = "json_schema", name = outputContract.Name, strict = true, schema = outputContract.Schema } };
            request.Content = JsonContent.Create(payload);
            using var response = await SendAsync(request, timeout.Token, streaming: true);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            logger.LogInformation("AI inference transport returned HTTP {StatusCode}; stream header present {HasStreamHeader}",
                (int)response.StatusCode, string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase));
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadJsonAsync(response, timeout.Token);
                ThrowProviderError(response.StatusCode, ErrorCode(error));
            }
            // Some plan responses omit Content-Type. They still must contain a valid SSE
            // stream ending in response.completed; a JSON/body-only response cannot succeed.
            if (mediaType is not null && !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new AiProviderException(AiFailure.MalformedResponse);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream);
            var eventData = new StringBuilder();
            var output = new StringBuilder();
            var receivedCharacters = 0;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                receivedCharacters += line.Length;
                if (receivedCharacters > 2 * 1024 * 1024) throw new AiProviderException(AiFailure.MalformedResponse);
                if (line.Length == 0)
                {
                    var completion = ProcessEvent(eventData, output);
                    if (completion.Completed) return (output.ToString(), completion.ReturnedModel);
                    eventData.Clear();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    eventData.AppendLine(line[5..].TrimStart(' '));
                }
            }
            var lastEvent = ProcessEvent(eventData, output);
            if (lastEvent.Completed) return (output.ToString(), lastEvent.ReturnedModel);
            throw new AiProviderException(AiFailure.IncompleteResponse);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailure.ProviderUnavailable);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new AiProviderException(AiFailure.IncompleteResponse);
        }
    }

    private (bool Completed, string? ReturnedModel) ProcessEvent(StringBuilder eventData, StringBuilder output)
    {
        if (eventData.Length == 0) return (false, null);
        var data = eventData.ToString().TrimEnd();
        if (data == "[DONE]") throw new AiProviderException(AiFailure.IncompleteResponse);
        var body = ParseJson(data);
        var type = RequiredString(body, "type");
        if (type == "response.output_text.delta")
            output.Append(OptionalString(body, "delta") ?? throw new AiProviderException(AiFailure.MalformedResponse));
        else if (type == "response.completed")
        {
            if (!body.TryGetProperty("response", out var response) || OptionalString(response, "status") != "completed" || output.Length == 0)
            {
                logger.LogWarning("AI completion rejected; has response {HasResponse}, has output {HasOutput}",
                    body.TryGetProperty("response", out _), output.Length > 0);
                throw new AiProviderException(AiFailure.MalformedResponse);
            }
            return (true, OptionalString(response, "model"));
        }
        else if (type == "response.incomplete") throw new AiProviderException(AiFailure.IncompleteResponse);
        else if (type is "response.failed" or "error")
        {
            var error = body.TryGetProperty("response", out var response) ? response : body;
            ThrowProviderError(HttpStatusCode.BadRequest, ErrorCode(error));
        }
        return (false, null);
    }

    private async Task<JsonElement> GetDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (discovery is { } cached) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Get, ChatGptProtocol.Discovery);
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) ThrowProviderError(response.StatusCode, null);
        var configuration = await ReadJsonAsync(response, cancellationToken);
        if (RequiredString(configuration, "issuer") != ChatGptProtocol.Issuer ||
            RequiredString(configuration, "authorization_endpoint") != ChatGptProtocol.Authorize ||
            RequiredString(configuration, "token_endpoint") != ChatGptProtocol.Token)
            throw new AiProviderException(AiFailure.MalformedResponse);
        discovery = configuration;
        return configuration;
    }

    private async Task RefreshKeysAsync(JsonElement configuration, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TrustedAuthEndpoint(configuration, "jwks_uri"));
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) ThrowProviderError(response.StatusCode, null);
        var body = await ReadJsonAsync(response, cancellationToken);
        try
        {
            var keys = new JsonWebKeySet(body.GetRawText());
            signingKeys = keys.Keys.Where(key => key.Kty is "RSA" or "EC").Cast<SecurityKey>().ToList();
            if (signingKeys.Count == 0) throw new AiProviderException(AiFailure.InvalidIdentity);
            keysExpireAt = timeProvider.GetUtcNow().AddMinutes(30);
        }
        catch (ArgumentException) { throw new AiProviderException(AiFailure.InvalidIdentity); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool streaming = false)
    {
        try
        {
            return await httpClient.SendAsync(request,
                streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailure.ProviderUnavailable);
        }
        catch (HttpRequestException) { throw new AiProviderException(AiFailure.ProviderUnavailable); }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Error bodies may contain private diagnostics and are never surfaced or logged.
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        try { return ParseJson(content); }
        catch (AiProviderException) when (!response.IsSuccessStatusCode)
        {
            ThrowProviderError(response.StatusCode, null);
            throw;
        }
    }

    private static JsonElement ParseJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new AiProviderException(AiFailure.MalformedResponse);
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new AiProviderException(AiFailure.MalformedResponse); }
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string endpoint, string accessToken)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static Uri TrustedAuthEndpoint(JsonElement configuration, string property)
    {
        if (!Uri.TryCreate(RequiredString(configuration, property), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "https" || endpoint.Host != "auth.openai.com" || !endpoint.IsDefaultPort ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new AiProviderException(AiFailure.MalformedResponse);
        return endpoint;
    }

    private static string RequiredString(JsonElement body, string property) =>
        OptionalString(body, property) is { Length: > 0 } value ? value : throw new AiProviderException(AiFailure.MalformedResponse);

    private static string? OptionalString(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? ErrorCode(JsonElement body)
    {
        if (body.TryGetProperty("error", out var error))
            return error.ValueKind == JsonValueKind.String ? error.GetString() : OptionalString(error, "code");
        return OptionalString(body, "code");
    }

    private static void ThrowProviderError(HttpStatusCode status, string? code)
    {
        var failure = code switch
        {
            "subscription_sharing_usage_limit_exceeded" => AiFailure.UsageLimitReached,
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => AiFailure.ProviderUnavailable,
            "model_not_found" => AiFailure.ModelUnavailable,
            _ => status switch
            {
                HttpStatusCode.Unauthorized => AiFailure.ReconnectRequired,
                HttpStatusCode.TooManyRequests => AiFailure.UsageLimitReached,
                HttpStatusCode.NotFound => AiFailure.ModelUnavailable,
                >= HttpStatusCode.InternalServerError => AiFailure.ProviderUnavailable,
                _ => AiFailure.ProviderRejected
            }
        };
        throw new AiProviderException(failure);
    }

    internal static bool FixedEquals(string expected, string? actual) => actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
