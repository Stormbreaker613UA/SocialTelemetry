using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Application.Features.People.UpdateExternalConnection;

public sealed class Input : ExternalConnectionInput
{
    public Guid PersonId { get; init; }
    public Guid ConnectionId { get; init; }
}
