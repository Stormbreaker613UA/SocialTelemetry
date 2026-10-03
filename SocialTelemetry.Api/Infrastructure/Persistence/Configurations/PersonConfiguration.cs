using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialTelemetry.Api.Domain.People;

namespace SocialTelemetry.Api.Infrastructure.Persistence.Configurations;

public sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.HasKey(person => person.Id);

        builder.Property(person => person.DisplayName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(person => person.RelationshipContext)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.HasOne(person => person.UserProfile)
            .WithMany(userProfile => userProfile.People)
            .HasForeignKey(person => person.UserProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(person => person.Facts)
            .WithOne(fact => fact.Person)
            .HasForeignKey(fact => fact.PersonId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(person => person.Inferences)
            .WithOne(inference => inference.Person)
            .HasForeignKey(inference => inference.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
