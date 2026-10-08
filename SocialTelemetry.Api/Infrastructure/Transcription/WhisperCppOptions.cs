using System.ComponentModel.DataAnnotations;

namespace SocialTelemetry.Api.Infrastructure.Transcription;

public sealed class WhisperCppOptions
{
    public string? FfmpegExecutable { get; set; }
    public string? WhisperExecutable { get; set; }
    public string? ModelFile { get; set; }
    [Required, MaxLength(200)] public string ModelId { get; set; } = "multilingual-whisper";
    public string? TemporaryDirectory { get; set; }
    [Range(1, 50 * 1024 * 1024)] public int InputBytes { get; set; } = 10 * 1024 * 1024;
    [Range(1, 3600)] public int DurationSeconds { get; set; } = 600;
    [Range(65536, 128 * 1024 * 1024)] public int DecodedBytes { get; set; } = 32 * 1024 * 1024;
    [Range(1, 1800)] public int TimeoutSeconds { get; set; } = 300;
    [Range(1, 4)] public int Concurrency { get; set; } = 1;
    [Range(1, 1024 * 1024)] public int ProcessOutputCharacters { get; set; } = 65536;
    [Range(50, 1000)] public int FileCheckMilliseconds { get; set; } = 100;
    [Range(1, 120000)] public int ResultCharacters { get; set; } = 12000;

    public bool HasConsistentLimits() => DecodedBytes >= (long)(DurationSeconds + 1) * 32000 + 65536;
}
