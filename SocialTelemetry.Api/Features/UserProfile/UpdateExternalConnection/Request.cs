using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.UserProfile.UpdateExternalConnection;

public sealed class Request : ExternalConnectionInput
{
    public Guid ConnectionId { get; init; }
}
