namespace SocialTelemetry.Api.Domain.Users;

public sealed class UserProfileExternalConnection
{
    public Guid Id { get; set; }
    public Guid UserProfileId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string? ExternalUserId { get; set; }
    public string? Handle { get; set; }
    public string? DisplayName { get; set; }
    public string? ProfileUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
