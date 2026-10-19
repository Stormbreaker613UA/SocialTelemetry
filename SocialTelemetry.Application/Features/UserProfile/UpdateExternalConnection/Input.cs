using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Application.Features.UserProfile.UpdateExternalConnection;

public sealed class Input : ExternalConnectionInput
{
    public Guid ConnectionId { get; init; }
}
