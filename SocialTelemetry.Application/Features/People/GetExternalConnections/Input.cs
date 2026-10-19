using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Application.Features.People.GetExternalConnections;

public sealed class Input
{
    public Guid PersonId { get; init; }
}
