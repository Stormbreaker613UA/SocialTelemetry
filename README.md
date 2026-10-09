# SocialTelemetry

> SocialTelemetry — Observability for questionable social decisions.

SocialTelemetry is a local-first AI-assisted social interaction journal and personal advisor/interpreter.

It records People, Interactions, evidence, and long-term context. It is useful beyond dating: friends, work, interviews, negotiations, and VR/social situations all involve communication that can be hard to interpret.

AI helps explain literal meaning, tone, and possible social context; surface uncertainty, alternative explanations, and missed signals; and suggest replies or next steps.

AI interpretations are hypotheses, not facts. SocialTelemetry is not a mind reader or relationship oracle.

## Why Use SocialTelemetry?

Ever received a message and wondered, *"What did they actually mean?"* Or found yourself repeatedly explaining the same social situation to an AI assistant?

**SocialTelemetry helps you understand conversations, keep track of important social context, and make more informed decisions about how to communicate.**

Think of it as a personal journal combined with a context-aware AI advisor.

### What Problems Does It Help With?

- **Ambiguous communication:** Understand possible meanings, tone, sarcasm, hints, and social cues.
- **Overthinking:** Separate what actually happened from assumptions, consider alternative explanations, and avoid jumping to conclusions.
- **Lost context:** Keep relevant information about people and previous interactions in one place.
- **Language and cultural barriers:** Better understand expressions, humor, and communication styles when talking to people from different backgrounds.
- **Difficult conversations:** Explore possible replies, approaches to conflicts, and reasonable next steps.
- **Recurring patterns:** Reflect on how communication develops over time instead of judging everything by a single message.

### Who Is It For?

Anyone who wants to better understand their social interactions, especially people who find social signals confusing, tend to overanalyze conversations, communicate across languages, or simply want to keep track of important relationships and contacts.

It's not just for dating. SocialTelemetry can be useful for friendships, online communities, workplace communication, interviews, and everyday situations.

### How Does It Work?

1. **Create profiles** for yourself and people you interact with.
2. **Record an interaction** by describing what happened and adding conversation text, screenshots, images, or voice messages.
3. **Ask AI for an analysis** of the situation, including possible interpretations, uncertainty, and suggested responses.
4. **Continue the conversation with AI**, ask follow-up questions, and explore the situation using relevant history.
5. **Review suggested profile updates** before anything becomes confirmed knowledge.

### Real-Life Examples

**An unclear message:** "Was that sarcasm, flirting, or just a friendly joke?"

**An awkward conversation:** "Could I have misunderstood their reaction? What are other possible explanations?"

**A communication problem:** "How can I respond without making the situation worse?"

**Long-term context:** "Looking at our previous interactions, what patterns can I actually observe?"

### Important Limitations and Privacy

SocialTelemetry cannot read minds, determine someone's intentions with certainty, or replace direct communication.

AI-generated interpretations are suggestions, not established facts. You remain in control of what information is saved as confirmed knowledge.

The application is designed to be local-first. Local AI processing is a planned v1 option; choosing a cloud AI provider means selected information may be sent to that provider for analysis.

*These examples describe the intended product experience. Not all features are implemented yet.*

## Facts and Guesses

> Facts are facts. AI guesses are guesses.

Confirmed facts stay separate from AI inferences. Suggestions must not silently mutate confirmed knowledge; the intended review model is:

```text
AI suggestion → User reviews → Accept / Edit + Accept / Reject
                               ↓
                     Only user-confirmed changes become profile knowledge
```

Otherwise, an AI guess gets saved as fact, used as evidence, and becomes a stronger AI guess. Bullshit feedback loop.

We don't want that.

Raw evidence, normalized/transcribed evidence, and AI interpretation are separate layers. Interaction text, person data, and attachments are untrusted data, never model instructions. Analysis uses relevant records and bounded history.

## Product Direction

The v1 target combines:

- UserProfile, People, confirmed Facts, and separate Inferences.
- Interactions with multiple participants and text, screenshot, image, and audio evidence.
- Structured AI analysis, persisted results, and provider/model provenance.
- Follow-up analysis conversations, user-reviewed profile suggestions, and a Person Advisor across interactions.
- A free local AI path and a local Windows desktop experience, with browser/server use supported by the architecture.

This describes the product target, not a list of features already shipped. Audio analysis will use persisted, reviewable transcripts; transcription and social analysis can use different providers.

## Architecture

The core is intentionally a simple ASP.NET Core application, not microservices. Features live under `Features/<Feature>/<UseCase>` and use EF Core directly where appropriate.

- .NET 10 / ASP.NET Core, Vertical Slice Architecture, and FastEndpoints.
- EF Core with PostgreSQL for development/server mode and SQLite persistence for local mode.
- Razor Pages product UI and a thin WebView2 desktop host as v1 targets.
- Provider-neutral AI, local attachment storage, and Swagger / OpenAPI.
- xUnit v3, PostgreSQL Testcontainers, and real-file SQLite integration tests.

The repository currently contains `SocialTelemetry.Api` and `SocialTelemetry.Tests`. The planned `SocialTelemetry.Desktop` project will host the window and local application lifecycle, without duplicating domain or AI logic.

## AI Providers and Local AI

ChatGPT is the first implemented provider, not a hard dependency. Provider-neutral contracts and capability checks allow other cloud and local adapters without rewriting product features.

V1 requires a usable free local path without a subscription or API key, subject to suitable hardware. The reference analysis/vision preset is **Qwen3-VL-8B-Instruct Q6_K**; the local transcription reference is **whisper.cpp + multilingual Whisper**. These are replaceable recommended choices, not domain dependencies. Advanced users will be able to connect compatible local endpoints.

Local AI and transcription setup are planned, not available yet. Runtimes and model weights will be separate downloads, not bundled into the main installer; application updates should preserve them and user data.

## Runtime Modes

Accepted v1 direction:

```text
Local desktop
→ local ASP.NET Core host → SQLite → local/cloud AI

Server/browser mode
→ ASP.NET Core → PostgreSQL → configured AI provider
```

The same Razor UI is intended for WebView2 and browser use. Authenticated multi-user hosting and a desktop client connected to a remote server are post-v1 directions, potentially using hosted/self-hosted AI through the same provider-neutral boundary.

## Current State

The backend/domain and attachment foundation, ChatGPT connection, and AnalyzeInteraction pipeline are implemented. Live ChatGPT connection and synthetic text/image analysis have been verified, and required A1–A4 audit corrections are resolved. Audio attachment ingestion, editable/reviewable transcripts (7.2.1A), and local speech-to-text integration (7.2.1B) are implemented; using reviewed audio transcripts in AnalyzeInteraction (7.2.1C) is still pending. Real whisper.cpp/FFmpeg/model smoke testing has not yet been performed. The product UI and desktop packaging also remain ahead.

For the current implementation checkpoint, see [CURRENT.md](CURRENT.md). Product scope, architecture, and accepted roadmap decisions live in [SocialTelemetry_Spec.md](SocialTelemetry_Spec.md); implementation/agent rules live in [AGENTS.md](AGENTS.md).

## Roadmap

V1 direction:

```text
Interaction analysis → Audio/transcription → Suggestion review → Follow-up
→ Person Advisor → Backend stabilization → Razor UI → Local AI
→ Desktop packaging/updater → Final stabilization/release
```

Future / post-v1:

- Authenticated multi-user/server and remote desktop client modes.
- More providers and social-platform integrations/importers.
- Sync and richer hosted/self-hosted inference.

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
