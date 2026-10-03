# SocialTelemetry

> Observability for questionable social decisions.

SocialTelemetry is a small side project born from a very simple engineering problem:

> Developer encountered an unfamiliar social situation.  
> Instead of behaving like a normal person, he built software.

The idea is to keep structured context about people and social interactions, attach additional evidence such as text, screenshots, images, or audio, and use AI to help analyze what actually happened.

This is NOT an AI oracle that tells you what another person is thinking.

The main rule is simple:

> Facts are facts. AI guesses are guesses.

## Origin Story

### Incident

An engineer received an ambiguous social signal.

### Expected behavior

Respond naturally.

### Actual behavior

Overanalysis.

### Root Cause

Engineer.

### Corrective Action

Build an ASP.NET Core application.

### Engineering Commission

- Lead Overthinking Engineer
- ChatGPT — External Technical Consultant

Decision: approved 2/2.

## What It Does

SocialTelemetry currently aims to support:

- User profile
- People profiles
- Confirmed facts about people
- Social interaction history
- Text / screenshot / image / audio attachments
- AI-assisted interaction analysis
- Follow-up questions about an analysis
- General AI advisor for a specific person
- AI-suggested profile updates
- User-controlled Accept / Edit / Reject for AI suggestions

## Important Design Rule

AI-generated interpretations must never silently become confirmed facts.

```text
AI assumption
    ↓
User reviews it
    ↓
Accept / Edit / Reject
    ↓
Only confirmed information becomes persistent knowledge
```

```text
AI guess
↓
saved as fact
↓
used as evidence
↓
stronger AI guess
↓
bullshit feedback loop
```

We don't want that.

## Tech Stack

- .NET 10
- ASP.NET Core
- FastEndpoints
- Vertical Slice Architecture
- Entity Framework Core
- PostgreSQL
- OpenAI integration
- Swagger / OpenAPI
- xUnit

## Current Architecture

```text
UserProfile
    ↓
People
    ↓
Interactions
    ├── Description
    ├── Participants
    ├── Text
    ├── Screenshots
    ├── Images
    └── Audio
         ↓
    AI Analysis
         ↓
    Observations
    Interpretations
    Uncertainty
    Missing Context
    Suggestions
         ↓
    User confirmation
```

## Philosophy

This project should stay:

- small
- explicit
- boring where possible
- AI-assisted, not AI-controlled
- user-controlled
- uncertainty-aware

If a pattern is not required by the current feature set, it probably doesn't belong here.

## Non-Goals

For now:

- No microservices
- No Kafka
- No RabbitMQ
- No Azure Service Bus
- No Redis
- No event sourcing
- No vector database
- No RAG
- No distributed relationship saga
- No MarriageService
- No Woman.Client
- No automatic life management

Yet.

## Known Issues

Human behavior is undocumented.

External systems may introduce breaking changes without notice.

Reality is not idempotent.

## Project Status

Experimental side project.

Built mostly for:

- learning
- experimenting with AI integration
- practicing VSA / FastEndpoints
- improving AI-assisted development workflow
- turning intrusive engineering thoughts into software

No promises.
No roadmap.
No investors.
No sanity.

## Final Note

> No, it didn't solve the social problem.

But now the problem has Swagger.
