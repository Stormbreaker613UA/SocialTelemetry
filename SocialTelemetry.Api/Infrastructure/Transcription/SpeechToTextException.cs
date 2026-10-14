namespace SocialTelemetry.Api.Infrastructure.Transcription;

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

public sealed class SpeechToTextException(SpeechToTextFailure failure) : Exception(failure switch
{
    SpeechToTextFailure.ConfigurationRequired => "Configure accessible local decoder, transcription executable and model files before transcribing.",
    SpeechToTextFailure.InvalidAudio => "The file is not a valid supported audio input.",
    SpeechToTextFailure.LimitExceeded => "Audio or transcription output exceeds the configured limits.",
    SpeechToTextFailure.DecoderFailed => "The local audio decoder could not complete.",
    SpeechToTextFailure.TranscriptionFailed => "Local transcription could not complete.",
    SpeechToTextFailure.InvalidResult => "Local transcription did not produce usable text.",
    SpeechToTextFailure.Busy => "Local transcription is busy. Retry when the current operation finishes.",
    SpeechToTextFailure.CleanupFailed => "Owned native process cleanup could not be confirmed within the configured time limit.",
    _ => "Local transcription exceeded its time limit."
})
{
    public SpeechToTextFailure Failure { get; } = failure;
    public int StatusCode => Failure switch
    {
        SpeechToTextFailure.ConfigurationRequired => 503,
        SpeechToTextFailure.InvalidAudio or SpeechToTextFailure.LimitExceeded => 400,
        SpeechToTextFailure.TimedOut => 504,
        SpeechToTextFailure.Busy => 429,
        _ => 502
    };
}
