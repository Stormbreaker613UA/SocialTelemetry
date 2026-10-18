using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Domain.Users;

namespace SocialTelemetry.Api.Infrastructure.Profiles;

// Local host selection is application state, never an authenticated server identity.
public sealed class LocalProfileSelection
{
    public int Id { get; set; } = 1;
    public Guid? UserProfileId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public bool LegacySingleProfileAccess { get; set; }
}

public sealed class LocalProfileSelectionConfiguration : IEntityTypeConfiguration<LocalProfileSelection>
{
    public void Configure(EntityTypeBuilder<LocalProfileSelection> builder)
    {
        builder.ToTable("LocalProfileSelection", table => table.HasCheckConstraint("CK_LocalProfileSelection_Singleton", "\"Id\" = 1"));
        builder.HasKey(selection => selection.Id);
        builder.Property(selection => selection.Id).ValueGeneratedNever();
        builder.Property(selection => selection.Version).IsConcurrencyToken();
        // Keep EF from nulling the optional selection when a tracked profile is deleted.
        // Database NO ACTION still rejects deletion until selection is explicitly handled.
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(selection => selection.UserProfileId).OnDelete(DeleteBehavior.ClientNoAction);
    }
}
