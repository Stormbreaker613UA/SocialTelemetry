using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Api.Domain.People;

namespace SocialTelemetry.Api.Infrastructure.Persistence.Configurations;

public sealed class PersonExternalConnectionConfiguration : IEntityTypeConfiguration<PersonExternalConnection>
{
    public void Configure(EntityTypeBuilder<PersonExternalConnection> builder)
    {
        builder.HasKey(connection => connection.Id);
        builder.Property(connection => connection.Platform).HasMaxLength(32).IsRequired();
        builder.Property(connection => connection.ExternalUserId).HasMaxLength(128);
        builder.Property(connection => connection.Handle).HasMaxLength(200);
        builder.Property(connection => connection.DisplayName).HasMaxLength(200);
        builder.Property(connection => connection.ProfileUrl).HasMaxLength(2048);
        builder.HasOne<Person>().WithMany().HasForeignKey(connection => connection.PersonId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(connection => new { connection.PersonId, connection.Platform, connection.ExternalUserId })
            .IsUnique().HasDatabaseName("IX_PersonConnection_StableIdentity");
    }
}
