using Microsoft.AspNetCore.Identity;

namespace SocialTelemetry.Api.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        Id = Guid.NewGuid();
    }

    public Guid UserProfileId { get; set; }
}
