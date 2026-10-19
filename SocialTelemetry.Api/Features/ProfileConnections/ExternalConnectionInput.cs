namespace SocialTelemetry.Api.Features.ProfileConnections;

public class ExternalConnectionInput
{
    public string Platform { get; init; } = string.Empty;
    public string? ExternalUserId { get; init; }
    public string? Handle { get; init; }
    public string? DisplayName { get; init; }
    public string? ProfileUrl { get; init; }
}

public sealed record ExternalConnectionResponse(Guid Id, Guid OwnerId, string Platform, string? ExternalUserId,
    string? Handle, string? DisplayName, string? ProfileUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
