# SocialTelemetry Current State

Updated: 2026-10-06

## Current Checkpoint

- **AI Pass 7.1 — COMPLETE.** No remaining technical blocker to starting 7.2.
- Provider-neutral AI foundation and ChatGPT OAuth Authorization Code + PKCE are complete.
- Protected local credential storage, serialized refresh, connection status/disconnect, and model discovery/selection are complete.
- F1 is complete: post-rotation identity validation failure clears the unusable session and requires reconnect, preserving registration/host identity.
- F2 is complete: neutral selected-model metadata/capabilities and completed-result provider/requested/returned-model provenance exist.
- 7.1 completion verification (before the test-stack migration): 135 total / 135 passed / 0 failed / 0 skipped; build: 0 warnings / 0 errors.
- Docker Compose PostgreSQL development environment works: `postgres:18`, verified version 18.6, healthy.
- All four existing EF migrations applied successfully; no model mismatch or new migration.
- Live smoke passed: startup, browser OAuth/callback, permission, models, selection (`gpt-5.6-sol`), restart persistence, disconnect/revocation, reconnect, safe errors, local-request protection, and cleanup. Exit code 0.
- Automated AI tests use fake provider responses; the live connection smoke was performed separately.
- .NET SDK pinned to stable `10.0.401` in `global.json`, with `latestPatch` servicing roll-forward and prereleases disabled; both projects remain `net10.0`.
- Direct dependencies reviewed: EF Core/Design/Relational aligned to `10.0.12`, Npgsql provider updated to `10.0.3`; other packages are already current stable versions.
- Testing stack migrated to xUnit v3 (`xunit.v3.mtp-v2` 4.0.1), MTP v2, and `coverlet.MTP` 10.1.0; existing HTTP/Testcontainers packages are unchanged.
- Root test command: `dotnet test --solution SocialTelemetry.slnx --no-build`; discovery adds `--list-tests`; coverage adds `--coverlet --coverlet-output-format cobertura --results-directory coverage`.
- Tooling verification: 135 tests discovered/passed, none failed/skipped; build: 0 warnings / 0 errors. Existing async test calls now pass test-context cancellation tokens; assertions and intentional cancellation scenarios are unchanged.
- AnalyzeInteraction / AiContextBuilder implementation has NOT started.

## Current Task

Next checkpoint: **AI Pass 7.2 — AnalyzeInteraction.**

## Immediate Next Steps

1. Build provider-neutral `AiContextBuilder` with bounded, owned context: UserProfile, participants/People, confirmed facts, relevant inferences, current Interaction, and selected/relevant previous interactions where appropriate.
2. Include optional focused `UserQuestion`, text evidence, screenshot/image evidence, and speaker-aware normalized evidence.
3. Implement deferred F3 concretely: capability validation and application-owned image/structured-output contracts; never silently discard unsupported evidence.
4. Produce validated, structured uncertainty-aware results, including translation, literal meaning, social meaning, and tone when relevant.
5. Preserve execution provenance and explicit prompt/schema versioning; protect against stale, failed, cancelled, incomplete, or invalid results.
6. Resolve F5 connection-lifetime/late-result safety before exposing analysis persistence.
7. Build and run relevant/full tests for the implemented scope. Do not expand v1 scope.

## Accepted Verification Exceptions

These are accepted NOT EXERCISED checks, not blockers to closing 7.1:

- Natural token refresh was not observed; token expiry was not manipulated. Refresh behavior has automated coverage.
- Tiny live inference was not exercised because no safe HTTP/manual execution surface exists yet.
- Neutral selected-model metadata was not manually exercised over HTTP because no endpoint exposes it; automated coverage exists.

## Guardrails

- `AGENTS.md` defines coding/agent rules; `SocialTelemetry_Spec.md` defines product/architecture/roadmap decisions.
- `AI_7_1_Architecture_Audit.md` is a historical audit; F1/F2 are resolved, F3/F5 belong to 7.2, and F4 waits for another provider.
- Keep provider independence, ownership, bounded context, evidence boundaries, and facts versus AI inferences intact.
- Store only validated results; keep credentials, prompts, and private evidence out of logs and checkpoint documents.
- No speculative framework, architecture redesign, or premature implementation of later passes.
