namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class ProfileStorageOptions
{
    // Null preserves the existing default relative to the attachment directory.
    public string? AvatarDirectory { get; set; }
}
