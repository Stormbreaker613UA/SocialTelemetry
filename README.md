# SocialTelemetry

> SocialTelemetry — Observability for questionable social decisions.

SocialTelemetry is a local AI-assisted journal and personal AI advisor/interpreter for understanding social interactions.

The journal records people and notable interactions, keeps long-term context, and lets you attach notes, screenshots, images, and audio files. Confirmed facts stay separate from assumptions and AI interpretations.

The planned AI workflow helps explain literal meaning, tone, and possible social meaning; surfaces uncertainty and alternative explanations; and suggests replies or next steps. Follow-up questions and a Person Advisor will help explore an analysis and patterns across interactions.

AI interpretations are hypotheses, not facts. SocialTelemetry is not a mind reader or relationship oracle.

## Current Status

An experimental side project targeting single-user local use. The backend and ChatGPT connection foundation are implemented; AI analysis and the user interface are still ahead.

Implemented:

- **Backend:** .NET 10 / ASP.NET Core, FastEndpoints, Vertical Slice Architecture, EF Core, PostgreSQL, and Swagger / OpenAPI.
- **People and context:** People CRUD, UserProfile, confirmed PersonFacts, and a separate persisted PersonInference model. Inference generation is not implemented yet.
- **Journal:** Interactions with multiple participants.
- **Attachments:** text, images, screenshots, and audio; metadata retrieval, file download, and deletion through local storage.
- **Attachment recovery:** a crash-safe lifecycle with staging, Pending / Ready / Deleting states, startup reconciliation, and cleanup of old orphan files.
- **API errors and logging:** global exception handling, ProblemDetails, and structured Serilog logging that avoids private content and credentials.
- **ChatGPT connection:** OAuth Authorization Code + PKCE, protected local credential storage, token refresh, model discovery, and model selection.
- **Tests:** xUnit, HTTP integration tests with PostgreSQL Testcontainers, temporary attachment directories, and fake provider responses for AI connection tests.

The ChatGPT integration implementation is complete in automated tests, but **real-account manual verification is still pending**. AnalyzeInteraction, suggested profile updates, follow-up conversations, and Person Advisor are not implemented yet. The project is not production-ready.

## Facts and Guesses

> Facts are facts. AI guesses are guesses.

AI-generated interpretations must never silently become confirmed PersonFacts. The planned suggestion workflow requires explicit user review:

```text
AI suggestion → User reviews → Accept / Edit + Accept / Reject
                               ↓
                     Only user-confirmed changes become facts
```

Otherwise, an AI guess gets saved as fact, used as evidence, and becomes a stronger AI guess. Bullshit feedback loop.

We don't want that.

Interaction text, person data, and attachments are untrusted data, never model instructions. Future AI context will use relevant records and bounded history rather than loading everything.

## Architecture

V1 intentionally uses one ASP.NET Core application project and one test project:

- `SocialTelemetry.Api` — HTTP endpoints, domain models, persistence, local storage, and AI provider integration.
- `SocialTelemetry.Tests` — automated application and integration tests.

Features live under `Features/<Feature>/<UseCase>`. FastEndpoints handles HTTP, and small slices use EF Core directly. This keeps the application easy to follow and appropriate for its current scope.

## Roadmap

Next:

1. Real ChatGPT connection smoke test
2. AnalyzeInteraction + AiContextBuilder + structured AI output
3. Suggested profile updates + follow-up
4. Person Advisor
5. Razor Pages UI
6. SQLite local mode
7. Self-contained Windows app
8. Installer / GitHub Releases
9. Manual update checker
10. V1 stabilization

Future / post-v1:

- VRChat/social-platform people import
- Other AI providers
- Audio transcription
- Multi-user/server mode expansion
- Sync

## Origin Story

> Developer encountered an unfamiliar social situation.\
> Instead of behaving like a normal person, he built software.

| Incident review | Finding |
|---|---|
| Incident | An engineer received an ambiguous social signal. |
| Expected behavior | Respond naturally. |
| Actual behavior | Overanalysis. |
| Root cause | Engineer. |
| Corrective action | Build an ASP.NET Core application. |

Engineering Commission:

- Lead Overthinking Engineer
- ChatGPT — External Technical Consultant

Decision: approved 2/2.

It started as a joke. Then the joke got persistence, structured outputs, and a product roadmap.

## Philosophy

Keep it small, explicit, boring where possible, AI-assisted, user-controlled, and honest about uncertainty.

If a pattern is not required by the current feature set, it probably doesn't belong here.

No microservices, messaging infrastructure, event sourcing, vector database, or RAG. No distributed relationship saga. No MarriageService. No Woman.Client. No automatic life management.

Yet.

## Known Issues

Human behavior is undocumented.

External systems may introduce breaking changes without notice.

Reality is not idempotent.

## Final Note

> No, it didn't solve the social problem.

But now the problem has Swagger.
