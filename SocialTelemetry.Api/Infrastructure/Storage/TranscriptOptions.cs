using System.ComponentModel.DataAnnotations;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class TranscriptOptions
{
    [Range(1, 120000)] public int TextCharacters { get; set; } = 12000;
}
