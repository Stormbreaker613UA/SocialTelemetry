using System.ComponentModel.DataAnnotations;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed class LocalAccessOptions
{
    [Range(12, 256)] public int MinimumPasswordLength { get; set; } = 12;
    [Range(1, 100)] public int FailedAttempts { get; set; } = 5;
    [Range(1, 1440)] public int LockoutMinutes { get; set; } = 5;
    [Range(1, 100)] public int AuthenticationAttemptsPerMinute { get; set; } = 10;
    [Range(1, 1440)] public int IdleMinutes { get; set; } = 30;
    [Range(1, 24)] public int AbsoluteHours { get; set; } = 8;
    [Range(1, 30)] public int RememberDays { get; set; } = 14;
    [Range(1, 60)] public int ExpirationCheckSeconds { get; set; } = 1;
    public string DataDirectory { get; set; } = Runtime.ApplicationPaths.DefaultCredentialDirectory("local-access");
}
