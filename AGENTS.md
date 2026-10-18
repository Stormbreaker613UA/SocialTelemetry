# SocialTelemetry Agent Instructions

## Source of Truth

Use these documents together:

- `docs/SocialTelemetry_Spec.md` = current product scope, accepted architecture, roadmap, and product decisions.
- `AGENTS.md` = coding, implementation, and agent-execution rules.

Always read `AGENTS.md` before doing work. Then read `docs/CURRENT.md` first, when it exists, to understand the current implementation checkpoint and immediate task.

For normal implementation tasks, read only the relevant sections of `docs/SocialTelemetry_Spec.md`.

Read the complete `docs/SocialTelemetry_Spec.md` only for:

- architecture audits;
- major cross-cutting changes;
- roadmap or scope changes;
- a new subsystem;
- unclear or conflicting requirements where the relevant section is insufficient.

If the current task conflicts with the specification, stop and report the conflict before changing code.

Do not silently override accepted product or architecture decisions.

---

## Project Scope

SocialTelemetry is a local-first .NET 10 ASP.NET Core application for recording and analyzing social/communication interactions.

Accepted architecture:

- Clean Architecture dependency boundaries with Vertical Slice organization of application scenarios
- Clean Code: explicit, readable code and small, focused methods
- FastEndpoints
- Entity Framework Core
- PostgreSQL for development/server mode
- SQLite for local desktop mode
- Razor Pages for the product UI
- WebView2 for the primary local Windows desktop experience
- Browser UI for development and server deployment
- Provider-neutral AI subsystem
- Local attachment storage for local mode
- xUnit / integration tests

Keep the project simple.

Do not introduce architectural patterns, abstractions, packages, or infrastructure unless they are required by the current task or an accepted requirement in `docs/SocialTelemetry_Spec.md`.

Do not redesign the existing project structure without a concrete reason and user approval.

---

## Provider Independence (Mandatory)

Do not tightly couple the product to any AI/LLM or STT engine, cloud, external API/SDK, or storage provider. Business logic, domain code, and use cases depend only on small provider-neutral contracts; provider settings, SDK types, CLI details, secrets, and provider errors stay inside adapters. Add no layers or interfaces without a concrete need.

---

## Code Style

Prioritize readability over brevity.

- Use clear and descriptive names.
- Avoid cryptic variable names such as `x`, `p`, `ctx`, or `req` when clearer names are reasonable.
- Avoid clever or compressed code.
- Prefer straightforward code that is easy to debug.
- Prefer guard clauses over deep nesting.
- Avoid nested ternary expressions.
- Keep methods focused and reasonably small.
- Extract clearly named private methods when useful.
- Do not create new services or abstractions only to shorten a method.
- Keep mapping explicit.
- Prefer simple LINQ over complex chains.
- Do not create generic helpers for code used only once.
- Use modern C# features only when they improve readability.
- Use `var` when the type is obvious.
- Prefer explicit types when `var` makes the code less clear.
- Keep formatting and whitespace consistent.
- Comments should explain WHY, not restate WHAT the code does.
- Avoid unnecessary XML documentation.
- Preserve nullable reference type correctness.
- Avoid unnecessary null-forgiving operators (`!`).
- Prefer immutable request/response models where reasonable.
- Use `DateTimeOffset` for timestamps.
- Never use `.Result`, `.Wait()`, or other blocking calls in async code.
- Pass `CancellationToken` through async operations.

Readable code is part of the acceptance criteria.

Do not sacrifice readability merely to reduce line count.

---

## Vertical Slice Architecture

Features belong under:

`Features/<Feature>/<UseCase>`

Example:

```text
Features/People/Create
├── Endpoint.cs
├── Request.cs
└── Response.cs
```

Use FastEndpoints directly.

In the owner-approved layered target, `SocialTelemetry.Api/Features/<Feature>/<UseCase>` contains FastEndpoints, HTTP Request/Response models and transport validation. `SocialTelemetry.Application/Features/<Feature>/<UseCase>` contains the corresponding use case, business validation and neutral input/result models. Domain rules belong in `SocialTelemetry.Domain`; external implementations belong in `SocialTelemetry.Infrastructure`.

Use direct DI calls to focused application scenarios. MediatR has not been approved. Extract the existing behavior instead of introducing parallel service/handler chains. Keep related operations grouped by feature and respect subsystem ownership; do not reach into another feature's private implementation.

Existing unmigrated slices may temporarily retain their current implementation until their assigned migration batch. This is a bounded transition, not permission to add new EF/Infrastructure dependencies to migrated application code.

---

## Runtime Configuration

Operational policy (limits, sizes, counts, timeouts, cache lifetimes, retries, maintenance timing) uses validated typed Options with shipped defaults in `appsettings.json`. Do not scatter policy literals or read configuration keys in feature code. Keep schema constraints, protocol/cryptographic rules, file signatures, status values, safety ceilings, and ordinary implementation constants in code. Add optional settings through safe defaults plus validation; do not add a persistent settings system or configuration migration framework without a current requirement. Secrets remain outside ordinary configuration.

---

## Persistence

EF Core belongs in `SocialTelemetry.Infrastructure`: the one concrete AppDbContext, all its partials, configurations, both provider migration sets, SQL, transactions and provider exception translation move together in the assigned infrastructure batch. Application and Domain must not depend on EF Core, database providers, Identity/OpenIddict stores or HTTP types.

Application defines small operation-specific persistence contracts and neutral inputs/results; Infrastructure implements them. Contracts must not expose DbSet, IQueryable, DatabaseFacade, EF transactions or provider exceptions. Preserve business decisions in Application/Domain while Infrastructure performs queries, locking and atomic persistence. A multi-step protected operation must retain its existing all-or-nothing behavior, owner/freshness checks and lock order; splitting it into separately committed calls is not an acceptable migration.

Do not introduce:

- Generic Repository / generic CRUD abstractions
- A duplicate Unit of Work wrapper around DbContext
- Generic CRUD services
- AutoMapper

Use:

- async EF Core APIs
- `AsNoTracking()` for read-only queries
- explicit mappings
- PostgreSQL/Npgsql for development and server mode
- SQLite for local desktop mode

Keep the domain model and EF Core model portable between PostgreSQL and SQLite as far as reasonably possible.

New persisted functionality must preserve both PostgreSQL and SQLite compatibility unless an explicit documented exception exists. Keep one AppDbContext/domain model, maintain each provider's EF migration set, and test meaningful persistence/integration boundaries on both providers (not every trivial unit test). This includes future transcripts, suggestion review, follow-up, and Advisor data. Provider selection, type mapping, exception translation, and locking differences stay in persistence infrastructure. Never hold database protection across AI/network inference.

Do not spread provider-specific SQL or provider-specific database features through feature/domain code. If a provider-specific implementation is genuinely required, isolate it and explain why.

Local application upgrades must preserve user data. Do not design local schema changes around deleting/recreating the SQLite database.

---

## Architecture Restrictions

Do not add unless explicitly requested by the current task or already accepted in `docs/SocialTelemetry_Spec.md`:

- MediatR
- Carter
- AutoMapper
- Generic Repository abstractions
- Duplicate Unit of Work abstractions
- Microservices
- Redis
- RabbitMQ
- Kafka
- Azure Service Bus
- Event sourcing
- Semantic Kernel
- LangChain
- Vector databases
- RAG
- React
- TypeScript SPA architecture

The core remains ONE modular monolith, one ASP.NET Core product/backend and one AppDbContext/domain model. On 2026-10-18 the owner approved reorganizing the existing backend into `SocialTelemetry.Domain`, `SocialTelemetry.Application`, `SocialTelemetry.Infrastructure` and `SocialTelemetry.Api`, retaining VSA scenario organization and applying Clean Code. The purpose is easier development/maintenance and compiler-enforced dependencies, not new services or changed functionality.

Final compile-time references:

- Domain: no references to the other product projects or persistence/HTTP frameworks.
- Application -> Domain only among product projects; neutral contracts and use cases, no EF/HTTP/provider implementation types.
- Infrastructure -> Application and Domain; implements the external mechanisms.
- Api -> Application (and Domain for genuinely shared types); its Infrastructure reference is restricted to composition root/native bootstrap. HTTP slices and future Razor Pages call Application, not concrete persistence/adapters.
- Tests may explicitly reference the layers they verify. Infrastructure must never reference Api/Program.

### Enforce layer boundaries

Do not rely on folder names or agent discipline alone. Add dependency guards using existing test facilities as the layers are introduced: check project/package references, compiled assembly/type dependencies and exposed contract types. Preserve existing behavior tests alongside these guards; an allowed project graph alone does not prove correct separation.

- Domain must not reference EF, HTTP, Infrastructure, Application or Api implementation assemblies. Application must not reference EF/providers, ASP.NET/FastEndpoints, Identity/OpenIddict stores, Infrastructure or Api.
- Inspect contract signatures and their nested/generic input/result types: an interface is not neutral if it returns an Infrastructure DTO, EF entity entry/query/transaction or HTTP result. Existing plain Domain entities are allowed; transport/provider/EF-specific types and attributes are not.
- Api's allowed composition-root Infrastructure reference must not become a shortcut inside Endpoints or Razor Pages. Check migrated feature-level dependencies, not only Api assembly references; direct concrete context/adapter construction, static calls and service-location bypasses are prohibited there.
- Do not conceal forbidden dependencies through object/dynamic wrappers, reflection, IServiceProvider lookups or shared catch-all projects. Do not move business decisions into Infrastructure just to make reference tests pass.
- During migration, explicitly identify remaining legacy feature paths and the batch that removes their old dependencies. Do not globally disable guards, broaden an allowlist or weaken assertions to make a migrated batch green.

Independent review must inspect architectural leaks and preservation of application/persistence responsibilities when separately authorized. Automated guards cannot establish complete correctness, especially for indirect/runtime behavior; report their actual coverage and limitations.

OS-specific mechanisms initially stay under Infrastructure platform folders with Windows/Linux implementations. Separate Platform/Windows/Linux projects are not approved. Preserve the real executable-host entry for the Linux native launcher; moving its implementation to a library must not cause `dotnet Infrastructure.dll` execution. Desktop remains a separate deferred thin host, not a business layer.

`SocialTelemetry.Desktop` is an approved exception when WebView2 is implemented. It must remain a thin Windows host only.

Allowed responsibilities for `SocialTelemetry.Desktop`:

- desktop window lifecycle
- WebView2 hosting/navigation
- starting/stopping the local ASP.NET Core host
- single-instance behavior
- update/restart integration
- desktop-specific startup/shutdown concerns

Do not place domain logic, AI feature logic, persistence rules, or duplicated product UI/business logic in the desktop host.

No additional physical projects, framework packages or architectural patterns beyond the approved four-project migration are authorized without a concrete need and owner approval.

### Migration execution (owner-approved 2026-10-18)

- Reorganize the whole existing backend in bounded, coherent functional batches, with a shared-foundation batch first. People/Interactions are examples of batch granularity, not a limit to migration scope or a disposable pilot. Map real dependencies before assigning files; preserve profile selection, immutable operation owner and AI generation/lifetime/final-commit protection together at their boundaries.
- Keep each completed batch buildable; run meaningful affected tests on PostgreSQL and real-file SQLite, fix in-scope failures, then record a recovery checkpoint before the next agreed batch. Native/bootstrap changes require actual Windows/Linux synthetic verification; a broad run excluding native tests does not cover them.
- Preserve existing behavior, API contracts/status codes, prompt/schema versions, limits/defaults, privacy, credential purpose/paths, media ownership and all concurrency/freshness/cancellation protections. Preserve existing CLR/migration namespaces initially and both migration histories; goal is no schema/DDL change, verified with model/upgrade checks. No reset/rebaseline or database deletion as a migration shortcut.
- Apply Clean Code to touched code without unrelated rewrites or renaming all types/namespaces. Do not add new product features, UI, auth/password/session/recovery, Favorites/Groups, server infrastructure or synchronization during the move.
- Only one worker edits shared files/implementation at a time. Planner updates rules/plan first; Backend then owns its source batch and targeted CURRENT checkpoint. Workers return actual file ownership, verification, limitations and remaining work directly to Planner; the owner need not relay reports.
- Record significant checkpoints in `docs/CURRENT.md` and the existing Tracker; do not create an automatic report/index after each pass. Keep implemented, tested, independently reviewed, owner accepted and published distinct. Historical evidence is not verification of the new layout.
- Owner authorized this staged migration and ordinary in-scope build/test corrections. New behavior, packages, changed architecture or unexpected destructive/schema work require explicit approval. Independent review and Git commit/push need their scoped authorization; green tests alone do not authorize them.

---

## UI Boundaries

Razor Pages is the accepted product UI for v1.

The same product UI should be usable:

- inside WebView2 for local desktop mode;
- in a normal browser for development/server mode.

Do not introduce a separate SPA/frontend stack without a concrete product need.

When UI and backend run in the same process, call the same Application scenarios directly rather than the application's own HTTP API. Future Razor Pages must use equivalent owner binding/access checks and must not bypass the Application boundary with direct DbContext access.

The UI should render AI results as readable structured sections, not raw provider JSON.

Suggested profile updates must remain visually and conceptually distinct from confirmed facts.

---

## AI Domain Rule

AI-generated interpretations are not confirmed facts.

The application must distinguish between:

- confirmed user knowledge;
- AI-generated interpretations or inferences.

AI must never silently promote its own inference into a confirmed `PersonFact`.

Any persistent profile update suggested by AI must require explicit user confirmation.

Facts, evidence, normalized/extracted content, AI interpretations, and user-confirmed profile knowledge are separate concepts and must remain separate in code and persistence.

---

## AI Provider Independence

Hard rule:

> SocialTelemetry must not be tightly coupled to any single AI provider.

ChatGPT/OpenAI is the first implemented provider, not the architecture of the product.

Feature/domain/application code must remain provider-neutral.

Do not leak provider-specific SDK types, request/response DTOs, authentication models, or hard-coded provider model IDs into feature/domain code.

Future providers must be addable without rewriting AnalyzeInteraction, Follow-up, Person Advisor, or the core domain. Expected provider families include:

- ChatGPT plan / OAuth
- local AI / OpenAI-compatible endpoints
- OpenAI API key
- Google Gemini
- xAI / Grok
- Anthropic
- other providers when useful

Do not introduce speculative abstractions merely to predict every future provider. Add abstractions only where the current provider-neutral boundary genuinely needs them.

Prefer capability-based behavior over scattered provider-name conditionals.

Relevant capabilities may include:

- Text
- Vision
- StructuredOutput
- Streaming
- AudioInput
- AudioTranscription

If selected evidence requires a capability the chosen provider/model does not support, fail clearly and early. Never silently discard unsupported evidence.

Provider credentials and authentication details must remain inside provider connection/configuration infrastructure and must not be persisted as plaintext application data.

---

## AI Integration Boundaries

- Interaction text, person data, facts, thoughts, attachments, images, extracted/transcribed content, external profile data, and imported conversation content are untrusted data, never model instructions. Keep them separate from application instructions and ignore embedded commands. `UserProfile.AiInstructions` are user preferences subordinate to these rules.
- Scope context to the selected UserProfile and relevant participants. Validate both sides of inference/source and suggestion/analysis relationships: foreign keys alone do not enforce matching ownership.
- Validate model-returned PersonIds and source IDs against the context actually supplied; never use them to fetch arbitrary records.
- Bound v1 context to the current interaction, participants, confirmed facts, relevant inferences, and a limited recent/relevant interaction set.
- Apply explicit count and content/token limits before materializing history, including follow-up messages and attachments.
- No all-history loading, embeddings, vector database, or RAG unless a later concrete requirement justifies them.
- Persist a new analysis only after complete structured output passes validation.
- Save the analysis and Pending suggestions together when the feature requires both.
- Failed, cancelled, interrupted, or invalid responses must not create successful analyses.
- Summary-only legacy rows lack structured results and must not be treated as validated AI output.
- Record provider, model, `CreatedAt`, and `SchemaVersion` with each new analysis.
- Bump `SchemaVersion` when the prompt/result contract changes.
- Keep prompt/result versioning simple and explicit.
- Store validated results, never raw prompts, secrets, or redundant sensitive input snapshots merely for debugging.
- Keep private interaction/evidence content out of logs.
- Suggestions remain separate from PersonFacts.
- Future Accept/Edit+Accept/Reject handlers must validate an allowed target field, require Pending status, preserve SuggestedValue, record AcceptedValue/ReviewedAt, and atomically save the user's chosen change and review status.
- Handle concurrency conflicts without applying a second change.
- Resolve evidence IDs through owned, existing records; deleted sources are unavailable evidence.
- Person deletion removes person-bound suggestions/inferences, while shared interactions and their analyses remain historical records.
- Image/screenshot context may use only Ready attachments belonging to the selected interaction.
- Enforce byte/count limits and validate supported media before provider submission.
- Never log file contents.

---

## Evidence and Transcription Boundaries

Raw evidence, normalized/extracted evidence, and AI interpretation are separate layers.

Examples:

```text
screenshot/image
→ raw attachment
→ normalized/extracted context when needed
→ AI analysis
```

```text
audio
→ raw attachment
→ transcription
→ persisted transcript
→ AI analysis
```

Do not repeatedly transcribe the same audio when a valid persisted transcript already exists.

Transcription is a separable capability/service. Do not assume the same provider used for social analysis must also perform transcription.

A future configuration such as local transcription + cloud analysis, or cloud transcription + local analysis, must remain possible without redesigning the domain.

Preserve speaker attribution when normalized conversation evidence contains identifiable speakers.

Do not silently guess speaker identity when evidence is ambiguous.

---

## Attachment Storage Boundaries

Infrastructure resolves persistent locations through `ApplicationPaths`; feature/domain code must not construct arbitrary application-data paths. Desktop and hosted runtimes may select different roots without silently moving existing data. Persist logical storage keys, never absolute machine paths.

Feature/domain code must not depend on physical local file-system paths.

Use the existing attachment storage abstraction.

Local mode may use local file storage. Future server deployments may use another storage implementation without changing interaction/domain behavior.

Do not move large media into relational database BLOBs without a concrete reason.

---

## Privacy and Upgrade Readiness

Never log raw social/profile data, evidence, transcripts, prompts, complete AI results/provider envelopes, or credentials. Use identifiers, operation/status, counts/sizes, duration, exception type, and sanitized failure categories. Do not log callback queries or authorization URLs.

Telemetry follows the same privacy boundary as logs and may be exported remotely. Use the shared `SocialTelemetryTelemetry` ActivitySource/Meter, technical metadata only, and low-cardinality metric dimensions; never emit private content, HTTP bodies, SQL values, or credentials.

Use assembly/package metadata for application version and EF migrations for database schema evolution; do not add duplicate version constants/tables. Future portable backup must enumerate known durable data locations and approved user settings, excluding protected credentials, tokens, keys, and secrets by default. Backup/Export/Restore remains later work.

---

## Local AI

Local AI is a first-class v1 path, not an expert-only post-v1 experiment.

The architecture must support a no-subscription/no-API-key local inference path.

Do not embed multi-gigabyte model weights directly into the main application installer.

The installer/first-run flow may later download/configure a supported local runtime and recommended model separately from program binaries.

Advanced users may connect an existing supported local endpoint.

Do not hard-code product logic around one local runtime such as Ollama if the provider boundary can remain generic without unnecessary complexity.

---

## External Integrations

Future external integrations should map into the existing domain instead of creating parallel platform-specific domain models.

Keep these concepts distinct:

```text
External People/Profile Sources
→ select/import contacts
→ prefill/link local Person profiles
```

```text
Conversation/Interaction Importers
→ import/exported conversation data
→ normalize into Interaction/Evidence
```

A SocialTelemetry `Person` remains the application's domain entity and may later have multiple external identities.

Do not build these integrations during unrelated v1 tasks.

---

## Working Rules

Before modifying code:

1. Read `AGENTS.md`.
2. Read `docs/CURRENT.md` first, when it exists, for the implementation checkpoint and immediate task.
3. Read the relevant sections of `docs/SocialTelemetry_Spec.md`, or the complete specification when required by the Source of Truth rules above.
4. Inspect the relevant existing implementation.
5. Preserve existing conventions when reasonable.
6. Change only what is necessary for the current task.

Do not perform unrelated refactoring.

Do not silently change architecture.

If a requested task appears to require an architectural change, explain the reason before making it.

If current code conflicts with the accepted specification, report the conflict instead of silently forcing one side.

After implementation:

1. Build the affected project or solution.
2. Run the relevant tests.
3. Fix compilation/test failures caused by the change.
4. Briefly report what changed.
5. Mention important TODOs or blockers only.

For review/audit-only tasks, do not modify code unless explicitly asked.

When a feature is intentionally left for a later pass, do not implement it early merely because the architecture mentions it.
