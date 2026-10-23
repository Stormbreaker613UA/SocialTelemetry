using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Infrastructure.Persistence.Configurations;

public sealed class PersonGroupConfiguration : IEntityTypeConfiguration<PersonGroup>
{
    public void Configure(EntityTypeBuilder<PersonGroup> builder)
    {
        builder.HasKey(group => group.Id);
        builder.Property(group => group.Name).HasMaxLength(PersonGroup.MaximumNameLength).IsRequired();
        builder.HasOne(group => group.UserProfile).WithMany().HasForeignKey(group => group.UserProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PersonGroupMembershipConfiguration : IEntityTypeConfiguration<PersonGroupMembership>
{
    public void Configure(EntityTypeBuilder<PersonGroupMembership> builder)
    {
        builder.HasKey(membership => new { membership.GroupId, membership.PersonId });
        builder.HasIndex(membership => membership.PersonId);
        builder.HasOne(membership => membership.Group).WithMany().HasForeignKey(membership => membership.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(membership => membership.Person).WithMany().HasForeignKey(membership => membership.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
