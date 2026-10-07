using System.Text.RegularExpressions;

namespace SocialTelemetry.Api.Features.ProfileConnections;

public class ExternalConnectionInput
{
    public string Platform { get; init; } = string.Empty;
    public string? ExternalUserId { get; init; }
    public string? Handle { get; init; }
    public string? DisplayName { get; init; }
    public string? ProfileUrl { get; init; }

    public bool TryNormalize(out NormalizedConnection normalized)
    {
        normalized = new((Platform ?? string.Empty).Trim().ToLowerInvariant(), TrimOptional(ExternalUserId),
            TrimOptional(Handle), TrimOptional(DisplayName), TrimOptional(ProfileUrl));
        if (!Regex.IsMatch(normalized.Platform, "^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant) ||
            normalized.ExternalUserId?.Length > 128 || normalized.Handle?.Length > 200 ||
            normalized.DisplayName?.Length > 200 || normalized.ProfileUrl?.Length > 2048)
            return false;

        return normalized.ProfileUrl is null ||
            (Uri.TryCreate(normalized.ProfileUrl, UriKind.Absolute, out var url) &&
             url.Scheme is "http" or "https" && !string.IsNullOrEmpty(url.Host) && string.IsNullOrEmpty(url.UserInfo));
    }

    private static string? TrimOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record NormalizedConnection(string Platform, string? ExternalUserId, string? Handle,
    string? DisplayName, string? ProfileUrl);

public sealed record ExternalConnectionResponse(Guid Id, Guid OwnerId, string Platform, string? ExternalUserId,
    string? Handle, string? DisplayName, string? ProfileUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
