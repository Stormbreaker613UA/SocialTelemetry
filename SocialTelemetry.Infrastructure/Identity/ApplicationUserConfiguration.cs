using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(account => account.Id).ValueGeneratedNever();
        builder.HasIndex(account => account.UserProfileId).IsUnique();
        builder.HasOne<UserProfile>().WithOne()
            .HasForeignKey<ApplicationUser>(account => account.UserProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
