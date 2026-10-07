using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.UserProfile.GetExternalConnection;

public sealed class Request
{
    public Guid ConnectionId { get; init; }
}
