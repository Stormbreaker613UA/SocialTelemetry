namespace SocialTelemetry.Application.Features.LocalAccess.SignIn;

public sealed class Scenario(ILocalAccessManagement access)
{
    public Task<AccessChange> ExecuteAsync(PasswordSignIn input, CancellationToken cancellationToken)
    {
        LocalAccessInput.Check(input.Login, 256);
        LocalAccessInput.Check(input.Password);
        return access.SignInAsync(input, cancellationToken);
    }
}
