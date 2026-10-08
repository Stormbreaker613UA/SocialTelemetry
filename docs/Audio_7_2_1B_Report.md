# Audio 7.2.1B — Local Speech-to-Text Report

Date: 2026-10-09

## Implementation

- Added provider-neutral `ISpeechToTextClient` and the local whisper.cpp adapter, independent of ChatGPT and `IAiClient`.
- FFmpeg decodes WAV, MP3, M4A/AAC, OGG/Opus, and WebM audio into verified 16-bit PCM WAV, 16 kHz, mono. MIME types and filenames are not treated as proof of valid audio.
- Native execution uses explicit executable paths, argument lists, automatic language detection, cancellation, timeout, bounded concurrency/output, and isolated temporary files with cleanup.
- Added `POST /interactions/{interactionId}/attachments/{attachmentId}/transcribe`, protected by the existing local-request checks.
- Only owned Ready Audio attachments are accepted. Source-current usable transcripts are reused; newer edits, deleted attachments, and changed sources cause safe conflicts. Generated text remains Unreviewed and does not overwrite manual corrections.
- Updated configuration, safe exception mapping, tests, `CURRENT.md`, and local configuration instructions in `docs/LocalTranscription.md`.
- No new packages or database migrations were required for stage B.

## Configuration

Configure trusted FFmpeg and whisper.cpp executable paths and a multilingual Whisper model under `WhisperCpp`. No binaries or models are downloaded or bundled automatically. Manual transcripts remain usable when these dependencies are missing.

Default limits: 10 MiB input, 10 minutes audio, 300-second execution timeout, and one concurrent native transcription. Busy admission returns HTTP 429. See `docs/LocalTranscription.md` for configuration and optional real-binary smoke instructions.

## Verification

Results from the completed implementation pass; tests were not rerun merely to save this report:

| Check | Result |
| --- | --- |
| Adapter/native-process tests | 30 passed |
| Transcription HTTP tests | 48 passed |
| Complete automated suite | 702 total / 702 passed / 0 failed / 0 skipped |
| Solution build | 0 warnings / 0 errors |
| PostgreSQL Testcontainers and real-file SQLite | Passed |
| EF pending model changes, both providers | None |
| Real FFmpeg/whisper.cpp model smoke | Not performed; binaries/model were not configured |

Automated transcription tests use fake engine/process boundaries. Native-runner tests use SDK/system test processes, not a real speech model.

## Commits

- `d2812abb1028e54127570941abe3cf354d236a79` — Add persistent audio transcripts and review lifecycle (stage A).
- `34658aa743658b6c2fc16a1cfc8b5b412f73ef1c` — Implement local audio transcription (stage B; 17 files changed).
- `5ddf758cb3662fe1c96e462fd58df3e69cea38dc` — Clarify provider independence rules (`AGENTS.md` only).

## Remaining Work

- Optional real-binary/model smoke verification remains outstanding; actual recognition quality has not been verified.
- A hard process crash or operating-system permission failure can leave temporary files. Concurrency limits are per application host.
- Audio 7.2.1C has not started: integrate only reviewed, owned, source-current transcripts into bounded AnalyzeInteraction context, with transcript provenance/version and freshness protection.
- AnalyzeInteraction continues to reject Audio until stage C.

Both local audit files and the ignored UI prototype remain untouched and excluded from commits.
