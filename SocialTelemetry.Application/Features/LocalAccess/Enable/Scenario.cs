namespace SocialTelemetry.Application.Features.LocalAccess.Enable;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessChange> ExecuteAsync(ProtectionEnrollment input, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(input.Login, 256);
        LocalAccessInput.Check(input.Password);
        return access.EnableAsync(input, cancellationToken);
    }
}
