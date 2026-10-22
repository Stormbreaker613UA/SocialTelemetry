using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SocialTelemetry.Infrastructure.Identity;

public enum LocalProtectionStatus { Uninitialized, Enabled, Disabled }

public sealed class LocalDeviceTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = "";
    public byte[]? ProtectedTicket { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class LocalProfileGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Guid AccountId { get; set; }
    public Guid SecurityEpoch { get; set; }
    public string SecurityStamp { get; set; } = "";
    public Guid HostInstance { get; set; }
    public bool Remembered { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset SignedInAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class LocalDeviceTicketConfiguration : IEntityTypeConfiguration<LocalDeviceTicket>
{
    public void Configure(EntityTypeBuilder<LocalDeviceTicket> builder)
    {
        builder.HasKey(ticket => ticket.Id);
        builder.Property(ticket => ticket.TokenHash).HasMaxLength(64);
        builder.HasIndex(ticket => ticket.TokenHash).IsUnique();
    }
}

public sealed class LocalProfileGrantConfiguration : IEntityTypeConfiguration<LocalProfileGrant>
{
    public void Configure(EntityTypeBuilder<LocalProfileGrant> builder)
    {
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.SecurityStamp).HasMaxLength(256);
        builder.HasOne<LocalDeviceTicket>().WithMany().HasForeignKey(grant => grant.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(grant => grant.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(grant => new { grant.DeviceId, grant.AccountId });
    }
}
