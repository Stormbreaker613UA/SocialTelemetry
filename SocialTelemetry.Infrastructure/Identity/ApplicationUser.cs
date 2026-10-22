using Microsoft.AspNetCore.Identity;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        Id = Guid.NewGuid();
    }

    public Guid UserProfileId { get; set; }
    public LocalProtectionStatus LocalProtection { get; set; }
    public Guid AccessEpoch { get; set; } = Guid.NewGuid();
    public string? RecoveryHash { get; set; }
    public int RecoveryFailures { get; set; }
    public DateTimeOffset? RecoveryLockoutEnd { get; set; }
}
