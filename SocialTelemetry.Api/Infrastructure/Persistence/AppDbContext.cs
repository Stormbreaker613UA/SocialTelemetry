using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Api.Infrastructure.Observability;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

public sealed partial class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ProfileAvatarCleanup? avatarCleanup = null) : DbContext(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<StorageDatabaseIdentity> StorageDatabaseIdentities => Set<StorageDatabaseIdentity>();
    public DbSet<UserProfileExternalConnection> UserProfileExternalConnections => Set<UserProfileExternalConnection>();
    public DbSet<PersonExternalConnection> PersonExternalConnections => Set<PersonExternalConnection>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonFact> PersonFacts => Set<PersonFact>();
    public DbSet<PersonInference> PersonInferences => Set<PersonInference>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<InteractionParticipant> InteractionParticipants => Set<InteractionParticipant>();
    public DbSet<InteractionAttachment> InteractionAttachments => Set<InteractionAttachment>();
    public DbSet<InteractionAnalysis> InteractionAnalyses => Set<InteractionAnalysis>();
    public DbSet<AnalysisConversationMessage> AnalysisConversationMessages => Set<AnalysisConversationMessage>();
    public DbSet<SuggestedProfileUpdate> SuggestedProfileUpdates => Set<SuggestedProfileUpdate>();

    public async Task LockAnalysisContextAsync(Guid userProfileId, CancellationToken cancellationToken)
    {
        // Bulk source writes must acquire this protection explicitly; tracked async saves do so below.
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("Analysis context protection requires a transaction.");

        // PostgreSQL and SQLite share this upsert syntax. The unique key makes first use race-safe;
        // the no-op update protects this profile until commit. SQL stays inside persistence.
        var affectedRows = await Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AnalysisContextGuards" ("UserProfileId") VALUES ({userProfileId})
            ON CONFLICT ("UserProfileId") DO UPDATE SET "UserProfileId" = EXCLUDED."UserProfileId"
            """, cancellationToken);
        if (affectedRows != 1)
            throw new InvalidOperationException("Could not acquire analysis context protection.");
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // SQLite has no native database ActivitySource. Coarse persistence spans expose no commands/values.
        using var operation = Database.IsSqlite() ? SocialTelemetryTelemetry.Start("database.save", cancellationToken) : null;
        operation?.Activity?.SetTag("db.system.name", "sqlite");
        try
        {
            var rows = await SaveCoordinatedChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            operation?.Complete();
            return rows;
        }
        catch (Exception exception)
        {
            operation?.Fail(exception);
            throw;
        }
    }

    private async Task<int> SaveCoordinatedChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        var obsoleteAvatarKeys = avatarCleanup is null
            ? []
            : await avatarCleanup.FindObsoleteKeysAsync(this, cancellationToken);
        var hasExternalTransaction = Database.CurrentTransaction is not null;
        var sourceChanges = AnalysisSourceChanges();
        if (sourceChanges.Length == 0)
        {
            var savedRows = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (!hasExternalTransaction && avatarCleanup is not null)
                await avatarCleanup.DeleteUnreferencedAsync(this, obsoleteAvatarKeys);
            return savedRows;
        }

        if (Database.CurrentTransaction is not null)
        {
            await LockAnalysisSourceChangesAsync(sourceChanges, cancellationToken);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        await LockAnalysisSourceChangesAsync(sourceChanges, cancellationToken);
        var affectedRows = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (avatarCleanup is not null)
            await avatarCleanup.DeleteUnreferencedAsync(this, obsoleteAvatarKeys);
        return affectedRows;
    }

    private EntityEntry[] AnalysisSourceChanges()
    {
        var changes = new List<EntityEntry>();
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
            if (entry.State is EntityState.Added or EntityState.Deleted ||
                (entry.State == EntityState.Modified && contextProperties.Any(property => entry.Property(property).IsModified)))
                changes.Add(entry);
        }
        return changes.ToArray();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IMigrationsAssembly, ProviderMigrationsAssembly>();
        optionsBuilder.AddInterceptors(SqliteSnapshotTransactions.Instance);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.Entity<StorageDatabaseIdentity>().ToTable("StorageDatabaseIdentity", table =>
            table.HasCheckConstraint("CK_StorageDatabaseIdentity_Singleton", "\"Id\" = 1"));
        modelBuilder.Entity<StorageDatabaseIdentity>().HasKey(identity => identity.Id);
        modelBuilder.Entity<StorageDatabaseIdentity>().Property(identity => identity.Id).ValueGeneratedNever();
        modelBuilder.Entity<StorageDatabaseIdentity>().Property(identity => identity.StoreId).ValueGeneratedNever();
        modelBuilder.Entity<InteractionAnalysis>().Property(analysis => analysis.ResultJson)
            .HasColumnType(Database.IsSqlite() ? "TEXT" : "jsonb");
        modelBuilder.Entity<UserProfile>().Property(profile => profile.AvatarStorageKey).HasMaxLength(32).IsConcurrencyToken();
        modelBuilder.Entity<UserProfile>().Property(profile => profile.AvatarMimeType).HasMaxLength(32);
        modelBuilder.Entity<Person>().Property(person => person.AvatarStorageKey).HasMaxLength(32).IsConcurrencyToken();
        modelBuilder.Entity<Person>().Property(person => person.AvatarMimeType).HasMaxLength(32);

        modelBuilder.Entity<InteractionParticipant>()
            .HasKey(participant => new { participant.InteractionId, participant.PersonId });

        modelBuilder.Entity<AnalysisContextGuard>().ToTable("AnalysisContextGuards");
        modelBuilder.Entity<AnalysisContextGuard>().HasKey(guard => guard.UserProfileId);
        modelBuilder.Entity<AnalysisContextGuard>().Property(guard => guard.UserProfileId).ValueGeneratedNever();

        if (Database.IsSqlite())
        {
            // SQLite cannot order DateTimeOffset natively. UTC ticks retain precision and chronological ordering.
            var timestampConverter = new ValueConverter<DateTimeOffset, long>(
                value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset))
                        property.SetValueConverter(timestampConverter);
                    // SQLite ignores varchar length facets; preserve the model's persisted bounds.
                    if (property.GetMaxLength() is { } maximum)
                        modelBuilder.Entity(entity.Name).ToTable(table => table.HasCheckConstraint(
                            $"CK_{entity.GetTableName()}_{property.Name}_Length",
                            $"length(\"{property.Name}\") <= {maximum}"));
                }
            }
            modelBuilder.Entity<InteractionAnalysis>().ToTable(table => table.HasCheckConstraint(
                "CK_InteractionAnalyses_ResultJson", "\"ResultJson\" IS NULL OR json_valid(\"ResultJson\")"));
        }
    }
}
