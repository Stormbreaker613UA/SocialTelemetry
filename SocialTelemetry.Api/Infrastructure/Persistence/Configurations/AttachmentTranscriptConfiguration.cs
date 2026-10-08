using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Infrastructure.Persistence.Configurations;

public sealed class AttachmentTranscriptConfiguration : IEntityTypeConfiguration<AttachmentTranscript>
{
    public void Configure(EntityTypeBuilder<AttachmentTranscript> builder)
    {
        builder.HasKey(transcript => transcript.AttachmentId);
        builder.Property(transcript => transcript.AttachmentId).ValueGeneratedNever();
        builder.HasOne(transcript => transcript.Attachment).WithOne()
            .HasForeignKey<AttachmentTranscript>(transcript => transcript.AttachmentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Property(transcript => transcript.GeneratedText).HasMaxLength(120000);
        builder.Property(transcript => transcript.CorrectedText).HasMaxLength(120000);
        builder.Property(transcript => transcript.SourceStorageKey).HasMaxLength(32);
        builder.Property(transcript => transcript.SourceSha256).HasMaxLength(64);
        builder.Property(transcript => transcript.Provider).HasMaxLength(200);
        builder.Property(transcript => transcript.Model).HasMaxLength(200);
        builder.Property(transcript => transcript.TranscriptionVersion).HasMaxLength(200);
        builder.Property(transcript => transcript.Version).IsConcurrencyToken().ValueGeneratedNever();
    }
}
