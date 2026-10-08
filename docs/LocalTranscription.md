# Local audio transcription

Audio 7.2.1B uses a separate speech-to-text adapter. It does not require ChatGPT, a subscription, or an API key. Manual transcripts continue working without native dependencies. Transcript use in AnalyzeInteraction belongs to stage C and is not implemented here.

## Configuration

Install trusted FFmpeg and whisper.cpp `whisper-cli` builds yourself, and obtain a compatible **multilingual** Whisper model. No binaries/models are downloaded or bundled by the application. The reference candidate is large-v3-turbo-q5_0; another compatible multilingual model may be used.

Configure absolute, readable paths through environment variables or local configuration:

```powershell
$env:WhisperCpp__FfmpegExecutable = '<absolute path to ffmpeg.exe>'
$env:WhisperCpp__WhisperExecutable = '<absolute path to whisper-cli.exe>'
$env:WhisperCpp__ModelFile = '<absolute path to multilingual ggml model>'
$env:WhisperCpp__ModelId = '<short descriptive model identifier>'
dotnet run --project SocialTelemetry.Api --launch-profile http --no-build
```

`ModelId` identifies the configured model in persisted provenance; it is not a discovered model catalog. `whisper-cli-text-v1` records the adapter/output contract, not the installed binary's build version. Use `--language auto`; English-only models cannot provide multilingual detection.

Targets: WAV, MP3, M4A/AAC, OGG/Opus, and WebM audio. Filename/MIME are not validity checks: the adapter checks container signatures and asks FFmpeg to decode the first audio stream to verified 16-bit PCM WAV, 16 kHz, mono. Unsupported/corrupt inputs fail. Video streams are not transcribed; no speaker identity or diarization is inferred.

Defaults in `WhisperCpp`: 10 MiB compressed input, 600 seconds decoded audio, 32 MiB decoded WAV, 300 seconds total timeout, one concurrent operation per application host, 65,536 combined stdout/stderr characters, 100 ms output-file checks, and 12,000 result characters. Extra native jobs receive 429 `Busy`; there is no waiting queue. `Transcripts:TextCharacters` also bounds persisted results. Over-duration/oversized input is rejected, not silently transcribed as a shorter clip. FFmpeg decodes at most one extra second to detect the duration boundary.

`TemporaryDirectory` defaults to a dedicated OS-temp `SocialTelemetry.Transcription` directory. Overrides resolve through ApplicationPaths and cannot overlap attachments/avatars or their staging areas. Each execution has an isolated generated subdirectory; it is removed on ordinary completion/failure/cancellation. OS permission failures are warned safely; abrupt process/machine crashes can leave temporary files. Keep this area private to the application user. Native binaries are trusted local programs, not an OS sandbox; keep them updated.

The adapter starts processes without a shell, restricts FFmpeg to local files/expected demuxers, drains/discards stdout/stderr, checks output bounds, and terminates its owned native process tree on cancellation. No native diagnostics, transcripts, audio, or private paths are returned in errors/logs.

## Optional manual smoke

Use disposable, non-private synthetic speech, with existing database migrations and media ownership configured normally. This smoke is separate from the automated suite, which uses fake STT/process boundaries.

1. Create a synthetic Interaction and upload an Audio attachment using the existing API (`POST /interactions/{interactionId}/attachments`). Wait for `Ready`.
2. Send `POST /interactions/{interactionId}/attachments/{attachmentId}/transcribe` to the local API with `X-SocialTelemetry-Local: 1`. No request body is required.
3. Expect 201 for a new transcript: generated text, provider `whisper-cpp`, configured model ID, contract version, source digest, new concurrency version, and `Unreviewed` state.
4. Repeat the POST: expect 200 and `reused: true`, with unchanged transcript/version. Manual/corrected/reviewed source-current transcripts are also reused; there is no force-overwrite operation.
5. GET the existing `/transcript` route. Correct text with PUT plus `ExpectedVersion`; then confirm review with a separate PUT using the returned version and `ConfirmReviewed: true`.
6. Repeat with short synthetic inputs in the supported formats available in your FFmpeg build. Corrupt input/missing configuration must return safe ProblemDetails and leave existing transcripts intact.
7. Delete synthetic Interaction/attachments through the existing API; verify per-execution temporary directories were removed. Do not submit the audio to AnalyzeInteraction yet.

CLI behavior follows [whisper.cpp's official CLI source](https://github.com/ggml-org/whisper.cpp/blob/master/examples/cli/cli.cpp) and [FFmpeg documentation](https://ffmpeg.org/ffmpeg.html), including its [format](https://ffmpeg.org/ffmpeg-formats.html) and [protocol](https://ffmpeg.org/ffmpeg-protocols.html) allowlists. Real decoding/model quality depends on the configured builds/model; no real-binary smoke is implied by fake-process test success.
