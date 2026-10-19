using System.ComponentModel.DataAnnotations;

namespace SocialTelemetry.Application.Options;

public sealed class TranscriptOptions
{
    [Range(1, 120000)] public int TextCharacters { get; set; } = 12000;
}
