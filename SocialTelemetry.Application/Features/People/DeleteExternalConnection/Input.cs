using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Application.Features.People.DeleteExternalConnection;

public sealed class Input
{
    public Guid PersonId { get; init; }
    public Guid ConnectionId { get; init; }
}
