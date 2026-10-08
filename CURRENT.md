# SocialTelemetry Current State

Updated: 2026-10-09

## Current Checkpoint

- **AI Pass 7.1 — COMPLETE.** OAuth Authorization Code + PKCE, protected credentials, serialized refresh, connection lifecycle, and model discovery/selection passed automated and real-account verification.
- F1 refresh-rotation safety and F2 neutral selected-model/capability/execution-provenance boundaries are complete.
- **AI Pass 7.2 — AnalyzeInteraction COMPLETE.** `POST /interactions/{interactionId}/analyze` returns a typed persisted analysis; local requests require `X-SocialTelemetry-Local: 1`.
- Astra REQUIRED findings A1–A4 are resolved: endpoint request protection, commit-time context freshness, execution validity through commit, and SSE framing.
- Profile/storage completion is finished: shared physical `IFileStorage`, separate UserProfile/Person avatars, Person archive/unarchive, and owner-scoped external connections with optional stable IDs. Avatars/external links are not AI evidence or fingerprint inputs; importers/UI remain future work.
- **Product Foundation Gate COMPLETE.** `ApplicationPaths` preserves existing media/credential locations and supports a validated host data root; ownership/deletion and privacy/logging were audited. Shared-file cleanup, assembly-version availability, EF/config evolution, and backup readiness are verified. Backup/Export/Restore were not implemented.
- **OpenTelemetry diagnostics baseline COMPLETE.** Serilog remains with trace/span correlation; ASP.NET Core/HttpClient/native Npgsql tracing, HTTP/runtime metrics, and custom analysis/AI/storage telemetry are active. OTLP is optional/off by default, with privacy-safe metadata and no collector dependency.
- **SQLite / multi-provider persistence COMPLETE.** Validated `Persistence` options select PostgreSQL (default) or real-file SQLite using `ApplicationPaths`. One AppDbContext/domain model remains; separate EF migration sets preserve PostgreSQL history. SQLite startup migrates before reconciliation, with FKs, WAL/full durability, UTC timestamp ordering, and privacy-safe coarse persistence traces. Synchronization is not implemented.
- B1/R1/C1 are corrected: durable per-database StoreId and locator-bound media/staging ownership fail closed before reconciliation; explicit offline adoption/rebinding preserves legacy files. Startup establishes/verifies WAL for existing files; SQLite timeout validation applies only to SQLite in runtime/design time. Cooperative directory leases exclude conflicting owners/rebinding while hosts are active.
- **Audio 7.2.1A — COMPLETE.** GET/PUT transcripts for owned Ready Audio support manual text, separate generated/corrected text, version-checked review, edit invalidation, source key/digest, and optional STT provenance. Transcript writes use profile-scoped context guards; deletion cascades from attachments. Default text limit: 12,000 characters. `AddAttachmentTranscripts` migrations passed on both providers; development data/media were not changed. Transcript use in AnalyzeInteraction remains stage C.
- **Audio 7.2.1B — implemented and automated verification green.** Independent ISpeechToTextClient/whisper.cpp uses FFmpeg and verified 16-kHz mono PCM WAV; auto language detection, bounded native execution, safe failures, isolated temp cleanup, and 429 admission control. TranscribeAttachment reuses current manual/generated transcripts and rejects changed sources/newer edits before guarded persistence. New text stays Unreviewed. No migration or real-binary/model smoke was required/performed; configure trusted binaries/model and use docs/LocalTranscription.md for optional verification. AnalyzeInteraction still rejects Audio until C.
- Runtime configuration is centralized in validated typed Options backed by `appsettings.json`; shipped defaults preserve prior behavior. Uploads, analysis input/aggregate-result limits, maintenance age, and provider timing/buffer policy are configurable within code-defined safety ceilings.
- Provider-neutral `AiContextBuilder` supplies owned UserProfile/participant context, separates confirmed facts from inferences, and preserves unknown speakers rather than inventing attribution.
- Default context bounds: 10 participants, latest 20 facts/10 relevant inferences per Person, and at most 5 earlier interactions with overlapping participants and no unrelated People; 64,000 total context characters.
- Text and Ready image/screenshot evidence use attachment storage with default limits: 10 attachments, 12,000 characters per text attachment, 3 images, 5 MiB/image, 10 MiB images total. Audio is rejected explicitly.
- F3 is complete: neutral image bytes and structured-output schema contracts; Text/StructuredOutput and, when needed, Vision are checked before inference. Unknown capabilities are not supported.
- ChatGPT capability mapping is restricted to exact officially documented model IDs; no permanent discovery catalog or model-name heuristics. Completed SSE output remains mandatory even when the provider omits its Content-Type header.
- Structured uncertainty-aware results validate required fields, sizes, confidence, context IDs, and suggestion targets. Pending suggestions (Description/HowWeMet/Notes only) are saved atomically with the analysis; People/PersonFacts are never changed automatically.
- Versions: `interaction-analysis-v1` schema and `analyze-interaction-v1` prompt. Persisted provenance comes from completed execution, not a later selected-model lookup.
- SHA-256 context/evidence fingerprints are rechecked under per-UserProfile database guards shared with relevant source writes through commit. Guards are created on demand and acquired in key order; unrelated PostgreSQL profiles do not contend. SQLite has one short writer per file and deferred read snapshots; separate-connection tests verify stale rejection and commit protection. Inference remains outside database protection.
- F5 is complete: session generations/lifetime cancellation invalidate in-flight and late results on disconnect; reconnect cannot revive an old execution. No credential lock spans inference.
- A3 uses a provider-neutral execution lease retaining shared credential-store protection from final session validation through save/commit. Disconnect/reconnect from another host cannot invalidate that protected commit.
- PostgreSQL's original nine migrations are preserved; `20261008153159_AddStorageDatabaseIdentity` adds infrastructure identity only. SQLite adds `20261008153204_AddStorageDatabaseIdentity` after its baseline. Both models match snapshots and migrations apply in disposable tests. The existing development database/media were not modified: apply the new migration and explicitly adopt verified legacy media before normal startup (specification section 28).
- Automated verification: **702 total / 702 passed / 0 failed / 0 skipped**; focused STT checks: **30 adapter/process + 48 HTTP passed**; build: **0 warnings / 0 errors**. PostgreSQL Testcontainers, real-file SQLite, and existing transcript/attachment/analysis regressions ran. STT/AI automated tests use fakes; native-runner checks use only SDK/system test processes.
- Live synthetic text and screenshot analyses passed with `chatgpt-plan` / `gpt-5.6-sol`; typed results and persisted provider/model/schema/prompt/fingerprint were verified. Natural refresh occurred without expiry manipulation. Synthetic records/files were removed through normal deletion workflows; observed logs contained no evidence/prompts/tokens.
- Tooling remains .NET SDK `10.0.401` (`global.json`, latestPatch, no prereleases), `net10.0`, xUnit v3/MTP v2, and Docker Compose `postgres:18` (18.6 previously verified). Compose configuration validates; it was not started in this pass while local PostgreSQL uses port 5432.
- Root verification: `dotnet restore`, `dotnet build SocialTelemetry.slnx`, `dotnet test --solution SocialTelemetry.slnx --no-build`, and `docker compose config`.

## Current Task

Next checkpoint: **AI Pass 7.2.1C — Reviewed transcripts in AnalyzeInteraction.** A/B are implemented; C has not started.

## Immediate Next Steps

1. Integrate only owned, reviewed, source-current transcripts as bounded normalized evidence, including transcript provenance/version in freshness checks.
2. Preserve speaker attribution and capability-based provider boundaries. Do not assume ChatGPT-plan inference supplies transcription.

## Guardrails

- `AGENTS.md` defines execution/coding rules; `SocialTelemetry_Spec.md` defines accepted product/architecture/roadmap decisions.
- `AI_7_1_Architecture_Audit.md` is historical: F1/F2/F3/F5 are resolved; remaining provider-specific administration/error presentation can wait for another adapter.
- Keep provider independence, ownership, bounded context, evidence boundaries, and facts versus AI inferences intact.
- Store only validated results; keep credentials, raw prompts, private evidence, and provider envelopes out of logs/checkpoint documents.
- No speculative framework, architecture redesign, or early implementation of Follow-up, Accept/Edit/Reject, Advisor, UI, or Local AI. New persistence work must preserve both database providers.
- Audit A5 (bounded provider-response reads) remains deferred; it was not changed by the A3 pass.
