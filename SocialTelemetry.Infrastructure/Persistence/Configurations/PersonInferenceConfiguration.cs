using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Infrastructure.Persistence.Configurations;

public sealed class PersonInferenceConfiguration : IEntityTypeConfiguration<PersonInference>
{
    public void Configure(EntityTypeBuilder<PersonInference> builder)
    {
        builder.HasOne(inference => inference.SourceInteraction)
            .WithMany()
            .HasForeignKey(inference => inference.SourceInteractionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
