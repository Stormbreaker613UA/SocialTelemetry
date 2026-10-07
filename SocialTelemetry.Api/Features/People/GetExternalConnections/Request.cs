using SocialTelemetry.Api.Features.ProfileConnections;

namespace SocialTelemetry.Api.Features.People.GetExternalConnections;

public sealed class Request
{
    public Guid PersonId { get; init; }
}
