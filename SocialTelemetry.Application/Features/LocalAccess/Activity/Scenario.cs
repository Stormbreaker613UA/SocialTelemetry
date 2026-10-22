namespace SocialTelemetry.Application.Features.LocalAccess.Activity;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return access.ActivityAsync(cancellationToken);
    }
}
