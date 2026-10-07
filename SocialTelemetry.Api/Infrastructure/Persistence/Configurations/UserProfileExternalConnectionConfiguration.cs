using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Api.Domain.Users;

namespace SocialTelemetry.Api.Infrastructure.Persistence.Configurations;

public sealed class UserProfileExternalConnectionConfiguration : IEntityTypeConfiguration<UserProfileExternalConnection>
{
    public void Configure(EntityTypeBuilder<UserProfileExternalConnection> builder)
    {
        builder.HasKey(connection => connection.Id);
        builder.Property(connection => connection.Platform).HasMaxLength(32).IsRequired();
        builder.Property(connection => connection.ExternalUserId).HasMaxLength(128);
        builder.Property(connection => connection.Handle).HasMaxLength(200);
        builder.Property(connection => connection.DisplayName).HasMaxLength(200);
        builder.Property(connection => connection.ProfileUrl).HasMaxLength(2048);
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(connection => connection.UserProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(connection => new { connection.UserProfileId, connection.Platform, connection.ExternalUserId })
            .IsUnique().HasDatabaseName("IX_UserConnection_StableIdentity");
    }
}
