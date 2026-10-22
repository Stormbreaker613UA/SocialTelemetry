namespace SocialTelemetry.Application.Features.LocalAccess.ChangePassword;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessChange> ExecuteAsync(PasswordChange input, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(input.CurrentPassword);
        LocalAccessInput.Check(input.NewPassword);
        return access.ChangePasswordAsync(input, cancellationToken);
    }
}
