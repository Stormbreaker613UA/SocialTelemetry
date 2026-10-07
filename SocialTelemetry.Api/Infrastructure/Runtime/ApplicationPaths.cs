using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Infrastructure.Runtime;

public sealed class ApplicationPaths(IOptions<ApplicationDataOptions> options, IWebHostEnvironment environment)
{
    public string RootDirectory { get; } = options.Value.RootDirectory ?? environment.ContentRootPath;

    public string ResolveDataDirectory(string directory) => Path.GetFullPath(directory, RootDirectory);

    public string Attachments(AttachmentStorageOptions attachments) => ResolveDataDirectory(attachments.LocalDirectory);

    public string Avatars(ProfileStorageOptions profiles, AttachmentStorageOptions attachments) =>
        ResolveDataDirectory(profiles.AvatarDirectory ?? Path.Combine(attachments.LocalDirectory, "avatars"));

    // Credentials have an explicit, separate location; selecting a data root must not move sessions.
    public string ResolveCredentialDirectory(string directory) => Path.GetFullPath(directory);

    public static string DefaultCredentialDirectory(string providerDirectory) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocialTelemetry", providerDirectory);

    public static bool AreStorageAreasSeparate(string attachmentDirectory, string avatarDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string[] attachmentAreas = [Path.TrimEndingDirectorySeparator(attachmentDirectory), Path.Combine(attachmentDirectory, ".staging")];
        string[] avatarAreas = [Path.TrimEndingDirectorySeparator(avatarDirectory), Path.Combine(avatarDirectory, ".staging")];
        return !attachmentAreas.Any(attachment => avatarAreas.Any(avatar => string.Equals(attachment, avatar, comparison)));
    }

    public static bool IsValidDirectory(string? directory, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var fullPath = Path.GetFullPath(directory, rootDirectory);
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
