using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Application.Features.UserProfile.DeleteExternalConnection;

public sealed class Input
{
    public Guid ConnectionId { get; init; }
}
