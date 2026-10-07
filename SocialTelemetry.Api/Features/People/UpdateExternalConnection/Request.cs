using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.People.UpdateExternalConnection;

public sealed class Request : ExternalConnectionInput
{
    public Guid PersonId { get; init; }
    public Guid ConnectionId { get; init; }
}
