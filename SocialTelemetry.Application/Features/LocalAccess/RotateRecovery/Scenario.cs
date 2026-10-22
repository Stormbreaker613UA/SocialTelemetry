namespace SocialTelemetry.Application.Features.LocalAccess.RotateRecovery;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessChange> ExecuteAsync(string password, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(password);
        return access.RotateRecoveryAsync(password, cancellationToken);
    }
}
