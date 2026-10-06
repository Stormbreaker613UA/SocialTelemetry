namespace SocialTelemetry.Api.Infrastructure.AI;

// Retains execution validity through the caller's short persistence phase, never inference.
public interface IAiExecutionLease : IDisposable
{
    Task ValidateAsync(CancellationToken cancellationToken);
}
