using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.People.AddExternalConnection;

public sealed class Request : ExternalConnectionInput
{
    public Guid PersonId { get; init; }
}
