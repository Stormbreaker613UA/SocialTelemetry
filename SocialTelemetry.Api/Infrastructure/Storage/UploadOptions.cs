using System.ComponentModel.DataAnnotations;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class UploadOptions
{
    // Absolute allocation ceilings remain code-defined even when policy is configured.
    [Range(1, 20 * 1024 * 1024)] public int AvatarMaxBytes { get; set; } = 5 * 1024 * 1024;
    [Range(1, 50 * 1024 * 1024)] public int AttachmentMaxBytes { get; set; } = 10 * 1024 * 1024;
    [Range(1024, 4 * 1024 * 1024)] public int MultipartOverheadBytes { get; set; } = 1024 * 1024;

    public long AvatarRequestBodyBytes => (long)AvatarMaxBytes + MultipartOverheadBytes;
    public long AttachmentRequestBodyBytes => (long)AttachmentMaxBytes + MultipartOverheadBytes;
}
