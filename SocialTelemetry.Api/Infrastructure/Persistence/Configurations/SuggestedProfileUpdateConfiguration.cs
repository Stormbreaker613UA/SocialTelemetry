using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Api.Domain.People;

namespace SocialTelemetry.Api.Infrastructure.Persistence.Configurations;

public sealed class SuggestedProfileUpdateConfiguration : IEntityTypeConfiguration<SuggestedProfileUpdate>
{
    public void Configure(EntityTypeBuilder<SuggestedProfileUpdate> builder)
    {
        builder.Property(suggestion => suggestion.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsConcurrencyToken();

        builder.HasOne(suggestion => suggestion.Person)
            .WithMany()
            .HasForeignKey(suggestion => suggestion.PersonId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(suggestion => suggestion.InteractionAnalysis)
            .WithMany()
            .HasForeignKey(suggestion => suggestion.InteractionAnalysisId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
