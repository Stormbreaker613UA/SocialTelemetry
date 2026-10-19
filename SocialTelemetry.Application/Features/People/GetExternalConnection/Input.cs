using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Application.Features.People.GetExternalConnection;

public sealed class Input
{
    public Guid PersonId { get; init; }
    public Guid ConnectionId { get; init; }
}
