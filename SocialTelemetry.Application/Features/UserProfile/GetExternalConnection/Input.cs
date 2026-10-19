using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Application.Features.UserProfile.GetExternalConnection;

public sealed class Input
{
    public Guid ConnectionId { get; init; }
}
