using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Users;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonFact> PersonFacts => Set<PersonFact>();
    public DbSet<PersonInference> PersonInferences => Set<PersonInference>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<InteractionParticipant> InteractionParticipants => Set<InteractionParticipant>();
    public DbSet<InteractionAttachment> InteractionAttachments => Set<InteractionAttachment>();
    public DbSet<InteractionAnalysis> InteractionAnalyses => Set<InteractionAnalysis>();
    public DbSet<AnalysisConversationMessage> AnalysisConversationMessages => Set<AnalysisConversationMessage>();
    public DbSet<SuggestedProfileUpdate> SuggestedProfileUpdates => Set<SuggestedProfileUpdate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<InteractionParticipant>()
            .HasKey(participant => new { participant.InteractionId, participant.PersonId });
    }
}
