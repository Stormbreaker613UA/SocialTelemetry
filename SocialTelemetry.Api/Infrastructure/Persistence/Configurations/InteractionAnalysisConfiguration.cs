using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Infrastructure.Persistence.Configurations;

public sealed class InteractionAnalysisConfiguration : IEntityTypeConfiguration<InteractionAnalysis>
{
    public void Configure(EntityTypeBuilder<InteractionAnalysis> builder)
    {
        builder.Property(analysis => analysis.ResultJson).HasColumnType("jsonb");
        builder.Property(analysis => analysis.PromptVersion).HasMaxLength(64);
        builder.Property(analysis => analysis.ContextFingerprint).HasMaxLength(64);

        builder.HasOne(analysis => analysis.Interaction)
            .WithMany(interaction => interaction.Analyses)
            .HasForeignKey(analysis => analysis.InteractionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(analysis => analysis.ConversationMessages)
            .WithOne(message => message.InteractionAnalysis)
            .HasForeignKey(message => message.InteractionAnalysisId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
