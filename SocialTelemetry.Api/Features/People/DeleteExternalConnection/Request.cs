using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.People.DeleteExternalConnection;

public sealed class Request
{
    public Guid PersonId { get; init; }
    public Guid ConnectionId { get; init; }
}
