using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.Profiles;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed class LocalAccessRuntime(TimeProvider clock, IOptions<LocalAccessOptions> options)
{
    public Guid HostInstance { get; } = Guid.NewGuid();
    private readonly object gate = new();
    private DateTimeOffset window;
    private int attempts;

    public void TakeAuthenticationAttempt()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (now >= window.AddMinutes(1)) { window = now; attempts = 0; }
            if (++attempts > options.Value.AuthenticationAttemptsPerMinute)
                throw new LocalAccessException(LocalAccessFailure.LockedOut);
        }
    }

    public bool IsValid(LocalProfileGrant grant, ApplicationUser account)
        => !IsExpired(grant, account) && (grant.Remembered || grant.HostInstance == HostInstance);

    public bool IsExpired(LocalProfileGrant grant, ApplicationUser account)
    {
        var now = clock.GetUtcNow();
        return grant.Revoked || account.LocalProtection != LocalProtectionStatus.Enabled ||
            grant.SecurityEpoch != account.AccessEpoch || grant.SecurityStamp != account.SecurityStamp ||
            grant.ExpiresAt <= now || !grant.Remembered && grant.LastActivityAt.AddMinutes(options.Value.IdleMinutes) <= now;
    }
}
