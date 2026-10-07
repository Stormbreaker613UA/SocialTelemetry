using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.UserProfile.DeleteExternalConnection;

public sealed class Request
{
    public Guid ConnectionId { get; init; }
}
