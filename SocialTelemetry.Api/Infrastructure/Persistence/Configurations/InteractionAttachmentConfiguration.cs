using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Infrastructure.Persistence.Configurations;

public sealed class InteractionAttachmentConfiguration : IEntityTypeConfiguration<InteractionAttachment>
{
    public void Configure(EntityTypeBuilder<InteractionAttachment> builder)
    {
        builder.Property(attachment => attachment.Status)
            .HasConversion<int>()
            .HasDefaultValue(AttachmentStatus.Ready)
            .HasSentinel(AttachmentStatus.Ready)
            .IsConcurrencyToken();
    }
}
