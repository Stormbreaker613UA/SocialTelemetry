# SocialTelemetry Current State

Updated: 2026-10-07

## Current Checkpoint

- **AI Pass 7.1 — COMPLETE.** OAuth Authorization Code + PKCE, protected credentials, serialized refresh, connection lifecycle, and model discovery/selection passed automated and real-account verification.
- F1 refresh-rotation safety and F2 neutral selected-model/capability/execution-provenance boundaries are complete.
- **AI Pass 7.2 — AnalyzeInteraction COMPLETE.** `POST /interactions/{interactionId}/analyze` returns a typed persisted analysis; local requests require `X-SocialTelemetry-Local: 1`.
- Astra REQUIRED findings A1–A4 are resolved: endpoint request protection, commit-time context freshness, execution validity through commit, and SSE framing.
- Profile/storage completion is finished: shared physical `IFileStorage`, separate UserProfile/Person avatars, Person archive/unarchive, and owner-scoped external connections with optional stable IDs. Avatars/external links are not AI evidence or fingerprint inputs; importers/UI remain future work.
- **Product Foundation Gate COMPLETE.** `ApplicationPaths` preserves existing media/credential locations and supports a validated host data root; ownership/deletion and privacy/logging were audited. Shared-file cleanup, assembly-version availability, EF/config evolution, and backup readiness are verified. Backup/Export/Restore were not implemented; Audio has not started.
- Runtime configuration is centralized in validated typed Options backed by `appsettings.json`; shipped defaults preserve prior behavior. Uploads, analysis input/aggregate-result limits, maintenance age, and provider timing/buffer policy are configurable within code-defined safety ceilings.
- Provider-neutral `AiContextBuilder` supplies owned UserProfile/participant context, separates confirmed facts from inferences, and preserves unknown speakers rather than inventing attribution.
- Default context bounds: 10 participants, latest 20 facts/10 relevant inferences per Person, and at most 5 earlier interactions with overlapping participants and no unrelated People; 64,000 total context characters.
- Text and Ready image/screenshot evidence use attachment storage with default limits: 10 attachments, 12,000 characters per text attachment, 3 images, 5 MiB/image, 10 MiB images total. Audio is rejected explicitly.
- F3 is complete: neutral image bytes and structured-output schema contracts; Text/StructuredOutput and, when needed, Vision are checked before inference. Unknown capabilities are not supported.
- ChatGPT capability mapping is restricted to exact officially documented model IDs; no permanent discovery catalog or model-name heuristics. Completed SSE output remains mandatory even when the provider omits its Content-Type header.
- Structured uncertainty-aware results validate required fields, sizes, confidence, context IDs, and suggestion targets. Pending suggestions (Description/HowWeMet/Notes only) are saved atomically with the analysis; People/PersonFacts are never changed automatically.
- Versions: `interaction-analysis-v1` schema and `analyze-interaction-v1` prompt. Persisted provenance comes from completed execution, not a later selected-model lookup.
- SHA-256 context/evidence fingerprints are rechecked under per-UserProfile database guards shared with relevant source writes through commit. Guards are created on demand and acquired in key order; unrelated profiles do not contend. Inference remains outside database protection.
- F5 is complete: session generations/lifetime cancellation invalidate in-flight and late results on disconnect; reconnect cannot revive an old execution. No credential lock spans inference.
- A3 uses a provider-neutral execution lease retaining shared credential-store protection from final session validation through save/commit. Disconnect/reconnect from another host cannot invalidate that protected commit.
- Latest migration `20261007153015_AddProfileArchiveAndExternalConnections` follows `AddProfileAvatars`; all nine migrations are applied. The Compose database is up to date with no model mismatch; analysis guards remain per-UserProfile.
- Automated verification: **360 total / 360 passed / 0 failed / 0 skipped**; build: **0 warnings / 0 errors**. Existing PostgreSQL Testcontainers tests ran; automated AI tests use fakes, never the real provider.
- Live synthetic text and screenshot analyses passed with `chatgpt-plan` / `gpt-5.6-sol`; typed results and persisted provider/model/schema/prompt/fingerprint were verified. Natural refresh occurred without expiry manipulation. Synthetic records/files were removed through normal deletion workflows; observed logs contained no evidence/prompts/tokens.
- Tooling remains .NET SDK `10.0.401` (`global.json`, latestPatch, no prereleases), `net10.0`, xUnit v3/MTP v2, and Docker Compose `postgres:18` (18.6, healthy).
- Root verification: `dotnet restore`, `dotnet build SocialTelemetry.slnx`, `dotnet test --solution SocialTelemetry.slnx --no-build`, and `docker compose config`.

## Current Task

Next checkpoint: **AI Pass 7.2.1 — Audio ingestion / persisted transcription.** Audio has not started. Foundation work is closed unless a concrete defect or blocker is discovered.

## Immediate Next Steps

1. Add audio ingestion/transcription only when requested, persist valid transcripts, and reuse them rather than repeatedly transcribing the same evidence.
2. Preserve speaker attribution and capability-based provider boundaries. Do not assume ChatGPT-plan inference supplies transcription.

## Guardrails

- `AGENTS.md` defines execution/coding rules; `SocialTelemetry_Spec.md` defines accepted product/architecture/roadmap decisions.
- `AI_7_1_Architecture_Audit.md` is historical: F1/F2/F3/F5 are resolved; remaining provider-specific administration/error presentation can wait for another adapter.
- Keep provider independence, ownership, bounded context, evidence boundaries, and facts versus AI inferences intact.
- Store only validated results; keep credentials, raw prompts, private evidence, and provider envelopes out of logs/checkpoint documents.
- No speculative framework, architecture redesign, or early implementation of Follow-up, Accept/Edit/Reject, Advisor, UI, SQLite, or Local AI.
- Audit A5 (bounded provider-response reads) remains deferred; it was not changed by the A3 pass.
