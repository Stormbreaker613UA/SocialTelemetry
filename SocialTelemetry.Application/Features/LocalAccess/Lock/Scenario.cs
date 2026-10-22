namespace SocialTelemetry.Application.Features.LocalAccess.Lock;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessChange> ExecuteAsync(Guid userProfileId, CancellationToken cancellationToken)
    {
        return access.LockAsync(userProfileId, cancellationToken);
    }
}
