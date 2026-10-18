using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Api.Domain.Users;

namespace SocialTelemetry.Api.Infrastructure.AI;

// Non-secret selection metadata. Credentials and provider registration stay in the protected store.
public sealed class ProfileAiSelection
{
    public Guid UserProfileId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public Guid? ConnectionId { get; set; }
    public string? ModelId { get; set; }
    public Guid Generation { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; }
}

public sealed class ProfileAiSelectionConfiguration : IEntityTypeConfiguration<ProfileAiSelection>
{
    public void Configure(EntityTypeBuilder<ProfileAiSelection> builder)
    {
        builder.ToTable("ProfileAiSelections");
        builder.HasKey(selection => selection.UserProfileId);
        builder.Property(selection => selection.UserProfileId).ValueGeneratedNever();
        builder.Property(selection => selection.ProviderId).HasMaxLength(100);
        builder.Property(selection => selection.ModelId).HasMaxLength(200);
        builder.Property(selection => selection.Generation).IsConcurrencyToken();
        builder.HasIndex(selection => selection.ConnectionId).IsUnique();
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(selection => selection.UserProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
