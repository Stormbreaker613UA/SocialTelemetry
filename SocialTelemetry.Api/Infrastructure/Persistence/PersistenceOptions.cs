using System.ComponentModel.DataAnnotations;
using SocialTelemetry.Api.Infrastructure.Runtime;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

public sealed class PersistenceOptions
{
    public string Provider { get; set; } = "PostgreSql";
    public string SqliteFile { get; set; } = "socialtelemetry.db";
    [Range(1, 120)]
    public int SqliteTimeoutSeconds { get; set; } = 30;

    public bool HasValidSqlitePath(ApplicationPaths paths)
    {
        if (string.IsNullOrWhiteSpace(SqliteFile) || SqliteFile == ":memory:") return false;
        try
        {
            var file = paths.DatabaseFile(SqliteFile);
            return !Directory.Exists(file) &&
                !string.IsNullOrEmpty(Path.GetFileName(file)) && Path.GetFileName(file).IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                (!File.Exists(file) || !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) &&
                ApplicationPaths.IsValidDirectory(Path.GetDirectoryName(file), paths.RootDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
