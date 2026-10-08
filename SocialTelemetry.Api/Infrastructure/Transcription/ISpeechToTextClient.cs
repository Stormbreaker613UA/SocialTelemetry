namespace SocialTelemetry.Api.Infrastructure.Transcription;

public interface ISpeechToTextClient
{
    Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken);
}

public sealed record SpeechToTextResult(string Text, string ProviderId, string ModelId, string TranscriptionVersion);
