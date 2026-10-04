# SocialTelemetry — AI Pass 7.1 Report

Date: 2026-10-04

## 1. Files changed

- Added six endpoints and local-request protection under `Features/AiConnection`.
- Added ChatGPT OAuth, protected storage, HTTP transport, model discovery, and `ChatGptPlanAiClient` under `Infrastructure/AI`.
- Updated `IAiClient`; removed the unused `OpenAiClient` placeholder.
- Updated `Program.cs`, exception mapping, configuration, and `.gitignore`.
- Added three AI test files.
- Added `Microsoft.IdentityModel.JsonWebTokens` **8.23.0**.
- No database or migration changes.

## 2. OpenAI connection flow

Implemented the currently documented local/open-source flow: system browser → dynamic registration using `dynamic_agent_client` and stable host identity → loopback callback → token exchange. Subsequent connections reuse the issued client registration. [Official sign-in documentation](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)

The provider uses public `/v1/responses`, OAuth bearer tokens, `store=false`, and streaming. Results are accepted only after successful completion; partial or failed responses are discarded. [Inference requirements](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)

## 3. OAuth/PKCE

- PKCE S256, cryptographically random state and nonce.
- Expiring, single-use callback state.
- ID-token signature, issuer, audience, expiry, nonce, and account validation.
- Granted inference scopes checked explicitly.
- Callback: `http://127.0.0.1:5059/ai-connection/chatgpt/callback`.

## 4. Credential protection

Credentials are encrypted using ASP.NET Core Data Protection under `%LOCALAPPDATA%\SocialTelemetry\ChatGpt`. Windows protects the key ring with current-user DPAPI. Writes replace the encrypted state file atomically.

Tokens never appear in endpoint responses or application logs.

## 5. Connection endpoints

All paths start with `/ai-connection/chatgpt`.

| Method | Path | Purpose |
|---|---|---|
| POST | `/connect` | Open browser authorization |
| GET | `/callback` | Complete authorization |
| GET | `/status` | Safe connection metadata |
| DELETE | Base path | Disconnect/revoke |
| GET | `/models` | Discover available models |
| PUT | `/model` | Save selected model |

Except the callback, requests require `X-SocialTelemetry-Local: 1`. Access is restricted to loopback.

## 6. Token refresh

Refresh operations are serialized across requests and local processes. Rotated credentials are saved before releasing the lock, including when the initiating caller cancels. Revoked credentials require reconnection. Disconnect clears local tokens and reports whether remote revocation succeeded. [Session requirements](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions)

## 7. Model discovery

Models come from the documented provider endpoint. No permanent model list or silent fallback exists. Missing selections, unavailable models, empty catalogs, and provider failures are handled explicitly.

## 8. Tests

**129 total / 129 passed / 0 failed.**

Includes 45 new AI tests using fake HTTP responses—no real OpenAI calls.

## 9. Build

`dotnet build SocialTelemetry.slnx` succeeded: **0 warnings, 0 errors**.

Also fixed Serilog logger sharing between concurrent application/test hosts.

## 10. Connect your account

With your existing PostgreSQL setup running:

```powershell
dotnet run --project SocialTelemetry.Api --launch-profile http
```

In another terminal:

```powershell
$base = 'http://127.0.0.1:5059/ai-connection/chatgpt'
$headers = @{ 'X-SocialTelemetry-Local' = '1' }

Invoke-RestMethod "$base/connect" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{}'
```

Complete browser authorization and grant plan usage. Then discover and select a returned model:

```powershell
Invoke-RestMethod "$base/models" -Headers $headers

Invoke-RestMethod "$base/model" -Method Put -Headers $headers `
  -ContentType 'application/json' -Body '{"modelId":"<returned model id>"}'
```

## 11. Remaining limitations / Pass 7.2

- Real account authorization still needs your manual verification.
- Pass 7.2 must implement bounded context, structured-result validation, and supported image input.
- No analysis/product endpoints or AI data persistence were added.
- Non-Windows key storage currently relies on private filesystem permissions; Windows uses DPAPI.
