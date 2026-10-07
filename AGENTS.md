# SocialTelemetry Agent Instructions

## Source of Truth

Use these documents together:

- `SocialTelemetry_Spec.md` = current product scope, accepted architecture, roadmap, and product decisions.
- `AGENTS.md` = coding, implementation, and agent-execution rules.

Always read `AGENTS.md` before doing work. Then read `CURRENT.md` first, when it exists, to understand the current implementation checkpoint and immediate task.

For normal implementation tasks, read only the relevant sections of `SocialTelemetry_Spec.md`.

Read the complete `SocialTelemetry_Spec.md` only for:

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

- Vertical Slice Architecture
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

Do not introduce architectural patterns, abstractions, packages, or infrastructure unless they are required by the current task or an accepted requirement in `SocialTelemetry_Spec.md`.

Do not redesign the existing project structure without a concrete reason and user approval.

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

For small features, logic may live inside the Endpoint.

If feature logic becomes non-trivial, extract it only when doing so improves readability.

Do not introduce a separate handler, service, command, or query merely for architectural ceremony. Respect feature/subsystem ownership; use a small existing stable boundary instead of reaching into another feature's private implementation.

---

## Runtime Configuration

Operational policy (limits, sizes, counts, timeouts, cache lifetimes, retries, maintenance timing) uses validated typed Options with shipped defaults in `appsettings.json`. Do not scatter policy literals or read configuration keys in feature code. Keep schema constraints, protocol/cryptographic rules, file signatures, status values, safety ceilings, and ordinary implementation constants in code. Add optional settings through safe defaults plus validation; do not add a persistent settings system or configuration migration framework without a current requirement. Secrets remain outside ordinary configuration.

---

## Persistence

Use EF Core directly from feature code where appropriate.

Do not introduce:

- Repository pattern
- Unit of Work abstraction
- Generic CRUD services
- AutoMapper

Use:

- async EF Core APIs
- `AsNoTracking()` for read-only queries
- explicit mappings
- PostgreSQL/Npgsql for development and server mode
- SQLite for local desktop mode

Keep the domain model and EF Core model portable between PostgreSQL and SQLite as far as reasonably possible.

Do not spread provider-specific SQL or provider-specific database features through feature/domain code. If a provider-specific implementation is genuinely required, isolate it and explain why.

Local application upgrades must preserve user data. Do not design local schema changes around deleting/recreating the SQLite database.

---

## Architecture Restrictions

Do not add unless explicitly requested by the current task or already accepted in `SocialTelemetry_Spec.md`:

- MediatR
- Carter
- AutoMapper
- Repository abstractions
- Unit of Work abstractions
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

The core remains a VSA modular monolith. Do not split `SocialTelemetry.Api` into Domain/Application/Infrastructure or module class libraries without a concrete technical/runtime reason and user approval. Prefer logical boundaries; extract physical projects only when justified.

`SocialTelemetry.Desktop` is an approved exception when WebView2 is implemented. It must remain a thin Windows host only.

Allowed responsibilities for `SocialTelemetry.Desktop`:

- desktop window lifecycle
- WebView2 hosting/navigation
- starting/stopping the local ASP.NET Core host
- single-instance behavior
- update/restart integration
- desktop-specific startup/shutdown concerns

Do not place domain logic, AI feature logic, persistence rules, or duplicated product UI/business logic in the desktop host.

Logical boundaries now are preferred over creating many physical projects prematurely.

---

## UI Boundaries

Razor Pages is the accepted product UI for v1.

The same product UI should be usable:

- inside WebView2 for local desktop mode;
- in a normal browser for development/server mode.

Do not introduce a separate SPA/frontend stack without a concrete product need.

When UI and backend run in the same process, do not call the application's own HTTP API merely for architectural purity if direct same-process application logic / DbContext usage is simpler and consistent with the project.

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
2. Read `CURRENT.md` first, when it exists, for the implementation checkpoint and immediate task.
3. Read the relevant sections of `SocialTelemetry_Spec.md`, or the complete specification when required by the Source of Truth rules above.
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
