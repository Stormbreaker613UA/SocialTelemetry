namespace SocialTelemetry.Application.Features.LocalAccess.Recover;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessChange> ExecuteAsync(RecoveryInput input, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(input.Login, 256);
        LocalAccessInput.Check(input.RecoveryCode, 64);
        LocalAccessInput.Check(input.NewPassword);
        return access.RecoverAsync(input, cancellationToken);
    }
}
