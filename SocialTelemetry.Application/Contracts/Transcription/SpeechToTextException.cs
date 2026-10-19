namespace SocialTelemetry.Application.Contracts.Transcription;

public enum SpeechToTextFailure
{
    ConfigurationRequired,
    InvalidAudio,
    LimitExceeded,
    DecoderFailed,
    TranscriptionFailed,
    InvalidResult,
    TimedOut,
    Busy,
    CleanupFailed
}

public sealed class SpeechToTextException(SpeechToTextFailure failure) : Exception(failure.ToString())
{
    public SpeechToTextFailure Failure { get; } = failure;
}
