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

    public async Task LockAnalysisContextAsync(CancellationToken cancellationToken)
    {
        // Bulk source writes must acquire this protection explicitly; tracked async saves do so below.
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("Analysis context protection requires a transaction.");

        // Updating the singleton row holds database protection until commit, including collection changes.
        var affectedRows = await Set<AnalysisContextGuard>().Where(guard => guard.Id == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(guard => guard.Id, guard => guard.Id), cancellationToken);
        if (affectedRows != 1)
            throw new InvalidOperationException("The analysis context guard is missing. Apply database migrations.");
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (!HasAnalysisSourceChanges())
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        if (Database.CurrentTransaction is not null)
        {
            await LockAnalysisContextAsync(cancellationToken);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        await LockAnalysisContextAsync(cancellationToken);
        var affectedRows = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affectedRows;
    }

    private bool HasAnalysisSourceChanges()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            string[] contextProperties = entry.Entity switch
            {
                UserProfile => [nameof(UserProfile.DisplayName), nameof(UserProfile.AboutMe), nameof(UserProfile.CommunicationStyle),
                    nameof(UserProfile.Goals), nameof(UserProfile.Preferences), nameof(UserProfile.Boundaries), nameof(UserProfile.AiInstructions)],
                Person => [nameof(Person.UserProfileId), nameof(Person.DisplayName), nameof(Person.Age), nameof(Person.Gender),
                    nameof(Person.Description), nameof(Person.RelationshipContext), nameof(Person.HowWeMet), nameof(Person.Notes)],
                PersonFact => [nameof(PersonFact.PersonId), nameof(PersonFact.Value), nameof(PersonFact.Source), nameof(PersonFact.CreatedAt)],
                PersonInference => [nameof(PersonInference.PersonId), nameof(PersonInference.Value), nameof(PersonInference.Confidence),
                    nameof(PersonInference.SourceInteractionId), nameof(PersonInference.CreatedAt)],
                Interaction => [nameof(Interaction.UserProfileId), nameof(Interaction.Title), nameof(Interaction.Description),
                    nameof(Interaction.UserThoughts), nameof(Interaction.OccurredAt)],
                InteractionParticipant => [nameof(InteractionParticipant.InteractionId), nameof(InteractionParticipant.PersonId)],
                InteractionAttachment => [nameof(InteractionAttachment.InteractionId), nameof(InteractionAttachment.Type),
                    nameof(InteractionAttachment.Status), nameof(InteractionAttachment.TextContent),
                    nameof(InteractionAttachment.StorageKey), nameof(InteractionAttachment.MimeType)],
                _ => []
            };

            if (contextProperties.Length == 0) continue;
            if (entry.State is EntityState.Added or EntityState.Deleted) return true;
            if (entry.State == EntityState.Modified && contextProperties.Any(property => entry.Property(property).IsModified)) return true;
        }
        return false;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<InteractionParticipant>()
            .HasKey(participant => new { participant.InteractionId, participant.PersonId });

        modelBuilder.Entity<AnalysisContextGuard>().ToTable("AnalysisContextGuards");
        modelBuilder.Entity<AnalysisContextGuard>().Property(guard => guard.Id).ValueGeneratedNever();
        modelBuilder.Entity<AnalysisContextGuard>().HasData(new AnalysisContextGuard { Id = 1 });
    }
}
