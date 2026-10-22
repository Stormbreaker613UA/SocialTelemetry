namespace SocialTelemetry.Application.Features.LocalAccess.Disable;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessChange> ExecuteAsync(string password, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(password);
        return access.DisableAsync(password, cancellationToken);
    }
}
