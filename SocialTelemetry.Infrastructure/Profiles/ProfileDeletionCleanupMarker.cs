using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SocialTelemetry.Infrastructure.Profiles;

// An addressed deletion receipt, not a scan for unknown owners in a shared credential directory.
public sealed class ProfileDeletionCleanupMarker
{
    public Guid UserProfileId { get; set; }
}

public sealed class ProfileDeletionCleanupMarkerConfiguration : IEntityTypeConfiguration<ProfileDeletionCleanupMarker>
{
    public void Configure(EntityTypeBuilder<ProfileDeletionCleanupMarker> builder)
    {
        builder.ToTable("ProfileDeletionCleanupMarkers");
        builder.HasKey(marker => marker.UserProfileId);
        builder.Property(marker => marker.UserProfileId).ValueGeneratedNever();
    }
}
