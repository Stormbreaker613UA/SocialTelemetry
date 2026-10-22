namespace SocialTelemetry.Application.Features.LocalAccess.Status;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessState> ExecuteAsync(CancellationToken cancellationToken)
    {
        return access.StatusAsync(cancellationToken);
    }
}
