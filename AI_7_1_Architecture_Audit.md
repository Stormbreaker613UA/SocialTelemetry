# AI Pass 7.1 Architecture Audit

## Overall verdict

**READY AFTER SMALL FIXES**

The existing adapter is a usable foundation. OAuth, token storage, refresh, and OpenAI HTTP transport are isolated from the domain and from `IAiClient`. No architecture rewrite, additional project, provider framework, or schema change is required. Before 7.2, fix the post-rotation recovery gap and expose a small provider-neutral selected-model/capability/provenance boundary.

**Review basis:** full `AGENTS.md`, `CURRENT.md`, and `SocialTelemetry_Spec.md`; current AI implementation, connection endpoints, DI/configuration, relevant domain/EF types, and tests. Reviewed on 2026-10-05 at commit `6844cf3313202e63773b9afe07d9ccc808b90eab`. Findings below are based on code inspection, not newly executed reproductions.

The recorded checkpoint is **129/129 tests passed**, with a successful build. Build/tests were **not rerun during this documentation-only audit**. The real-account smoke test remains **pending**; 7.1 is not fully complete and 7.2 has not started.

Verified strengths:

- `SocialTelemetry.Api/Infrastructure/AI/IAiClient.cs` exposes application-owned request/response types and cancellation, with no tokens, SDK DTOs, HTTP endpoints, or OAuth parameters. `ChatGptPlanAiClient` obtains credentials internally. The single adapter registration in `SocialTelemetry.Api/Program.cs` is appropriate now; it does not require every future adapter to use OAuth or the internet.
- `SocialTelemetry.Api/Infrastructure/AI/ChatGptConnection.cs` implements PKCE S256, random expiring state/nonce, one-use callbacks, registration/account separation, inference-scope checks, serialized refresh, and disconnect with local credential clearing. `ChatGptHttpClient.ValidateIdentityAsync` checks signature, issuer, audience, expiry, subject, nonce where applicable, and authorized party.
- `SocialTelemetry.Api/Infrastructure/AI/ChatGptCredentialStore.cs` uses Data Protection, Windows DPAPI-protected keys, atomic encrypted-file replacement, and an exclusive file lock alongside its process semaphore. Credentials are outside EF/domain data. Non-Windows storage relies on filesystem permissions for the key ring; this is not equivalent to Windows DPAPI protection.
- `SocialTelemetry.Api/Features/AiConnection/LocalAiConnectionMiddleware.cs` checks loopback address/host, exact callback authority, and local-request headers/origin for other connection operations. Safe connection responses do not expose tokens or authorization URLs. `Program.cs` disables provider HTTP logging and filters identity-library diagnostics; `SocialTelemetry.Api/Common/Exceptions/GlobalExceptionHandler.cs` returns fixed safe errors and logs categories/identifiers rather than provider bodies or private content. No secret/prompt logging was found in the inspected paths.
- `ChatGptHttpClient.GenerateAsync` uses the public Responses endpoint with `store=false` and `stream=true`, accepting text only after a completed terminal response. Failed, incomplete, interrupted, and malformed streams do not return partial success. This matches the documented [ChatGPT-plan inference flow](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference). It does not yet validate an analysis schema or persist analyses, which belongs to 7.2.
- `SocialTelemetry.Api/Domain/Interactions/InteractionAnalysis.cs` already has provider/model/schema provenance and structured-result storage. `SocialTelemetry.Api/Infrastructure/AI/Models/InteractionAnalysisResult.cs` is provider-neutral and accommodates translation, literal/social meaning, uncertainty, alternatives, evidence interaction IDs, and proposed next steps. Multiple analyses are supported. The existing analysis/suggestion/inference configurations preserve the intended cascades and separation from confirmed facts; `SocialTelemetry.Tests/Infrastructure/Persistence/AiReadinessTests.cs` covers those behaviors.

## Findings

### F1. Post-rotation identity-validation failure can retain a consumed refresh token

**Severity: Required before 7.2**

**Files:**

- `SocialTelemetry.Api/Infrastructure/AI/ChatGptConnection.cs` — `GetTokensAsync`, especially lines 231–254.
- `SocialTelemetry.Api/Infrastructure/AI/ChatGptHttpClient.cs` — `ValidateIdentityAsync`, discovery/key retrieval.
- `SocialTelemetry.Tests/Infrastructure/AI/ChatGptTestApp.cs` — `FakeChatGptServer` refresh response.
- `SocialTelemetry.Tests/Infrastructure/AI/ChatGptConnectionTests.cs` — refresh tests.

**Current behavior:** after a successful refresh exchange, an optional replacement ID token is validated before the replacement credentials are saved. Validation can fetch discovery/signing keys. A temporary provider error or timeout here becomes `RefreshFailed`, leaving the old token set stored. The fake server omits ID tokens on refresh, so existing rotation tests do not exercise this branch.

**Why it matters:** the old refresh token may already have been consumed. Retrying it can fail or trigger reuse handling, although valid replacement credentials were received. Serialization alone does not cover this failure window. Official guidance requires using the latest replacement and serializing rotation; see [accounts and sessions](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions).

**Smallest correction:** distinguish failures before and after a successful exchange. If replacement identity validation fails after rotation, do not leave the old token eligible for another refresh. For this MVP, clear the unusable local token set and explicitly require reconnect; preserve registration/host identity and never activate unvalidated credentials. Add fake-provider tests for a refresh returning an ID token followed by key-fetch failure/timeout. Preserve the existing cancellation-safe save behavior on successful validation. No recovery queue or new credential state machine is necessary.

### F2. Selected-model capabilities and complete provenance have no neutral boundary

**Severity: Required before 7.2**

**Files:**

- `SocialTelemetry.Api/Infrastructure/AI/IAiClient.cs`.
- `SocialTelemetry.Api/Infrastructure/AI/Models/AiTextRequest.cs` — `AiTextResponse`.
- `SocialTelemetry.Api/Infrastructure/AI/ChatGptProtocol.cs` — `ChatGptModel` / `ChatGptModelCatalog`.
- `SocialTelemetry.Api/Infrastructure/AI/ChatGptConnection.cs` — model discovery, selection, and `GetInferenceSessionAsync`.
- `SocialTelemetry.Api/Infrastructure/AI/ChatGptHttpClient.cs` — `GetModelsAsync` / `GenerateAsync`.
- `SocialTelemetry.Api/Features/AiConnection/Models/Endpoint.cs`.
- `SocialTelemetry.Api/Features/AiConnection/SelectModel/Endpoint.cs`.

**Current behavior:** discovery and saved selection are available only through `ChatGptConnection` and ChatGPT-named DTOs. Catalog entries contain ID/display name, without capabilities. `IAiClient` has no selected-model metadata. Its result contains text and the requested model ID, but no provider identity; the completed response's model metadata is not used. No vendor SDK DTO leaks into the domain, and opaque model strings alone are not lock-in.

**Why it matters:** 7.2 must validate evidence capabilities and persist provenance. With the current surface, it would need to reach into ChatGPT connection types, guess capabilities from model names, or hardcode the provider. These are concrete upcoming consumers of the missing boundary.

**Smallest correction:** expose an application-owned selected-model descriptor containing a stable provider ID, opaque model ID, and supported capabilities, through a small neutral API next to/on `IAiClient`. Map ChatGPT data inside the adapter. Carry provider/model identity on completed inference results so persisted provenance comes from the execution, not a later mutable selection lookup. Use returned model identity where available; keep requested versus returned identity semantics explicit.

Only declare capabilities actually supported by the adapter/model; unknown must not mean supported. The current two-string request supports text, not image submission or enforced structured output. Do not invent catalog capability fields or a permanent list of model IDs. Add focused mapping/unsupported-capability/provenance tests. Keep provider-specific OAuth administration routes; a universal connection manager, registry, or rewritten settings API is unnecessary.

### F3. The inference request is deliberately text-only

**Severity: Can wait — implement with the concrete 7.2 evidence/output work**

**Files:** `SocialTelemetry.Api/Infrastructure/AI/Models/AiTextRequest.cs`, `SocialTelemetry.Api/Infrastructure/AI/IAiClient.cs`, `SocialTelemetry.Api/Infrastructure/AI/ChatGptHttpClient.cs` (`GenerateAsync`).

**Current behavior:** the request consists of `Instructions` and `Input` strings; transport emits one user text input. There is no image content or requested structured-output contract. Transport streaming is consumed internally and returned as one completed text result.

**Why it matters:** screenshots cannot pass through this contract as images, and a successful text response does not prove schema-valid analysis. These are current limitations, not evidence that OAuth has contaminated the inference abstraction.

**Smallest correction:** during 7.2, extend application-owned input/output types only for its actual text/image and structured-result needs, then map them inside the adapter. Check capabilities before submission, reject unsupported evidence, and validate complete structured output before persistence. Keep normalized speakers/evidence and context ownership in application code, separate from provider wire DTOs. A public streaming interface, chat framework, arbitrary tools, and all-provider schema machinery are not prerequisites.

### F4. Shared failure messages assume ChatGPT

**Severity: Can wait — fix when adding Local AI or another provider**

**Files:** `SocialTelemetry.Api/Infrastructure/AI/AiProviderException.cs` (`GetDetail`), `SocialTelemetry.Api/Common/Exceptions/GlobalExceptionHandler.cs`.

**Current behavior:** generic failure categories such as provider unavailable, malformed response, incomplete response, and model not selected produce ChatGPT-specific text; the usage-limit message includes ChatGPT's settings URL. The global handler exposes those fixed messages safely.

**Why it matters:** a second adapter reusing these categories would tell users to fix ChatGPT even for a local model. This is a real presentation-level coupling, but it does not force changes to analysis/domain logic and is not a current credential leak.

**Smallest correction:** make shared inference messages provider-neutral and keep ChatGPT-specific reconnect/usage guidance at the ChatGPT connection boundary. Do not introduce an exception hierarchy or accept arbitrary provider error text into ProblemDetails.

### F5. Disconnect does not invalidate an already acquired inference session

**Severity: Can wait — resolve in 7.2 before exposing analysis requests**

**Files:** `SocialTelemetry.Api/Infrastructure/AI/ChatGptPlanAiClient.cs` (`GenerateTextAsync`), `SocialTelemetry.Api/Infrastructure/AI/ChatGptConnection.cs` (`GetInferenceSessionAsync`, `DisconnectAsync`), `SocialTelemetry.Tests/Infrastructure/AI/ChatGptPlanAiClientTests.cs`.

**Current behavior:** a token/model snapshot leaves the storage lock and is used by `GenerateAsync`. Disconnect clears stored credentials and attempts remote revocation, but neither cancels that request nor invalidates its result. An in-flight request can continue; a request paused after session acquisition can attempt submission after local disconnect. Existing tests cover caller cancellation and credential clearing, not this interleaving.

**Why it matters:** remote renewable-session revocation should not be treated as guaranteed cancellation of an already acquired access token or stream. Once analyses are user-facing, disconnect must not allow a late result to be persisted as though the connection remained active. There is no analysis endpoint/persistence path today, so this does not block the connectivity smoke test.

**Smallest correction:** bind provider requests to the connection lifetime, cancel active requests on disconnect, and reject results from an invalidated connection before handing them to feature code. Keep this inside the adapter/connection boundary and add a deterministic paused-request/disconnect test. Do not build a job system or hold the credential file lock throughout streaming.

## Required before 7.2

1. Fix **F1**, with tests for post-exchange validation failure and preservation of existing refresh/cancellation guarantees.
2. Add the minimal neutral selected-model/capability metadata and completed-result provenance from **F2**. Test it without real provider calls. Leave OAuth credentials and registration models provider-specific.
3. Run the full tests and `dotnet build SocialTelemetry.slnx`, then complete the real-account 7.1 smoke test before opening 7.2.

No source move, new project, migration, provider factory, repository, or domain redesign is required. F3/F5 belong to 7.2 implementation before its analysis endpoint is considered usable, not to a speculative expansion of 7.1.

## Safe to defer

- **7.2:** image/structured-output transport extensions (F3), disconnect/inference lifetime handling (F5), and `AiContextBuilder` with bounded, owned, speaker-aware evidence. Existing provenance columns do not enforce successful structured validation; 7.2 must perform that validation before saving. Validate model-returned person/source IDs against supplied context; existing foreign keys alone do not establish matching ownership. No current AI context builder loads private history, so there is no observed cross-profile inference leak to repair now.
- **Local AI / second cloud adapter:** provider-specific discovery/authentication implementation, neutral common error wording (F4), and the smallest runtime provider-selection configuration actually needed. Neither `IAiClient` nor the domain requires OAuth or internet access. Do not force local endpoint settings/API keys into `ChatGptCredentialStore`.
- **Audio pass:** separate transcription capability, persisted transcript, then analysis. Do not route raw audio through the present text client or assume the analysis provider can transcribe. Current ChatGPT-plan preview excludes audio/video input and transcription endpoints; see [preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).
- **UI/settings:** render connection status, catalog, saved selection, and capabilities separately. `GetStatusAsync` reports locally stored credentials/scopes, not a fresh successful provider probe; label that accurately. Provider-specific connection DTOs are acceptable on explicitly ChatGPT-specific administration routes and should not become analysis DTOs.
- **SQLite/local packaging:** adapt PostgreSQL-specific `ResultJson` column configuration in `SocialTelemetry.Api/Infrastructure/Persistence/Configurations/InteractionAnalysisConfiguration.cs` when SQLite is implemented. This is a persistence-provider concern, not AI lock-in. Razor Pages and the accepted thin WebView2 desktop project require no changes in this audit. Reassess key-ring protection for any supported non-Windows/server deployment then.

The inspected tests in `SocialTelemetry.Tests/Infrastructure/AI/ChatGptConnectionTests.cs`, `ChatGptPlanAiClientTests.cs`, and `ChatGptTestApp.cs` use fake HTTP, protected temporary storage, and no real OpenAI calls. They cover connection state, callback/identity validation, local guards, secret-safe responses/logs, selection failure, refresh concurrency/cancellation, disconnect, and stream failures. They cannot establish real account eligibility, provider catalog/capabilities, or end-to-end browser connectivity.

## 7.1 manual smoke test

**Still pending. Do not mark 7.1 complete.**

Fix F1 before exercising real refresh. Complete F2 and automated verification before the smoke test as part of the agreed audit -> required changes -> build/tests -> manual smoke sequence. F3–F5 do not require expanding this connectivity test into 7.2 product functionality.

Manually verify browser authorization/callback, inference permission, model discovery/selection, a tiny non-private and non-persistent inference reaching completion, restart with protected credentials, refresh, disconnect/reconnect, and safe errors. Do not use private interaction content or record tokens in reports. Passing fake-provider tests does not substitute for these checks.

## Final recommendation

1. Review this report; obtain explicit authorization before implementing its recommendations.
2. Apply only F1 and F2, with focused tests. Preserve the existing architecture and provider-specific OAuth adapter.
3. Run the complete automated suite and `dotnet build SocialTelemetry.slnx`; fix only related failures.
4. Perform the real-account ChatGPT 7.1 smoke test.
5. Mark 7.1 complete only after that test succeeds.
6. Begin 7.2 AnalyzeInteraction using the neutral boundary; implement its concrete evidence/structured-output requirements and F5 without introducing another provider yet.

**Finding totals:** 0 Blocker; 2 Required before 7.2; 3 Can wait.

Only this audit report was created. The pre-existing staged deletion of `AI_PASS_7_1_REPORT.md` was left untouched. No implementation, documentation checkpoint update, migration, or commit was performed.
