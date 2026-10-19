using SocialTelemetry.Application.Features.ProfileConnections;

namespace SocialTelemetry.Application.Features.People.AddExternalConnection;

public sealed class Input : ExternalConnectionInput
{
    public Guid PersonId { get; init; }
}
