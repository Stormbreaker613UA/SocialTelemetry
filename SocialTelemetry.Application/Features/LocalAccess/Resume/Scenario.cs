namespace SocialTelemetry.Application.Features.LocalAccess.Resume;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessChange> ExecuteAsync(Guid userProfileId, Guid expectedVersion, CancellationToken cancellationToken)
    {
        return access.ResumeAsync(userProfileId, expectedVersion, cancellationToken);
    }
}
