namespace SocialTelemetry.Api.Infrastructure.Storage;

internal static class StorageDirectories
{
    public static string Attachments(AttachmentStorageOptions options, string contentRoot) =>
        Path.GetFullPath(options.LocalDirectory, contentRoot);

    public static string Avatars(ProfileStorageOptions options, AttachmentStorageOptions attachments, string contentRoot) =>
        Path.GetFullPath(options.AvatarDirectory ?? Path.Combine(attachments.LocalDirectory, "avatars"), contentRoot);

    public static bool IsValid(string? directory, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var fullPath = Path.GetFullPath(directory, contentRoot);
            return new[] { fullPath, Path.Combine(fullPath, ".staging") }.All(path =>
                !File.Exists(path) && (!Directory.Exists(path) ||
                    !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
