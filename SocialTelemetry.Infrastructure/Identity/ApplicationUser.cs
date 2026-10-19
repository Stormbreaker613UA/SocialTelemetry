using Microsoft.AspNetCore.Identity;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        Id = Guid.NewGuid();
    }

    public Guid UserProfileId { get; set; }
}
