using SocialTelemetry.Application.Observability;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Infrastructure.Storage;
using SocialTelemetry.Infrastructure.Observability;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Profiles;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed partial class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ProfileAvatarCleanup? avatarCleanup = null,
    LocalProfileOperation? profileOperation = null, TimeProvider? clock = null) : IdentityUserContext<ApplicationUser, Guid>(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<StorageDatabaseIdentity> StorageDatabaseIdentities => Set<StorageDatabaseIdentity>();
    public DbSet<UserProfileExternalConnection> UserProfileExternalConnections => Set<UserProfileExternalConnection>();
    public DbSet<PersonExternalConnection> PersonExternalConnections => Set<PersonExternalConnection>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonGroup> PersonGroups => Set<PersonGroup>();
    public DbSet<PersonGroupMembership> PersonGroupMemberships => Set<PersonGroupMembership>();
    public DbSet<PersonFact> PersonFacts => Set<PersonFact>();
    public DbSet<PersonInference> PersonInferences => Set<PersonInference>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<InteractionParticipant> InteractionParticipants => Set<InteractionParticipant>();
    public DbSet<InteractionAttachment> InteractionAttachments => Set<InteractionAttachment>();
    public DbSet<AttachmentTranscript> AttachmentTranscripts => Set<AttachmentTranscript>();
    public DbSet<InteractionAnalysis> InteractionAnalyses => Set<InteractionAnalysis>();
    public DbSet<AnalysisConversationMessage> AnalysisConversationMessages => Set<AnalysisConversationMessage>();
    public DbSet<SuggestedProfileUpdate> SuggestedProfileUpdates => Set<SuggestedProfileUpdate>();

    public async Task LockAnalysisContextAsync(Guid userProfileId, CancellationToken cancellationToken)
    {
        if (profileOperation?.Binding is { } binding && binding.UserProfileId != userProfileId)
            throw new SocialTelemetry.Application.Exceptions.NotFoundException();
        // Bulk source writes must acquire this protection explicitly; tracked async saves do so below.
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("Analysis context protection requires a transaction.");

        await ProtectLocalProfileOperationAsync(cancellationToken);

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
        if (localAccessMutation) return await SaveSecurityChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (HasAccountSecurityChanges()) return await SaveAccountSecurityChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        var obsoleteAvatarKeys = avatarCleanup is null
            ? []
            : await avatarCleanup.FindObsoleteKeysAsync(this, cancellationToken);
        var hasExternalTransaction = Database.CurrentTransaction is not null;
        var journalChanges = JournalChanges();
        if (journalChanges.Length == 0 && profileOperation?.Binding is null)
        {
            var savedRows = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (!hasExternalTransaction && avatarCleanup is not null)
                await avatarCleanup.DeleteUnreferencedAsync(this, obsoleteAvatarKeys);
            return savedRows;
        }

        if (Database.CurrentTransaction is not null)
        {
            await ProtectLocalProfileOperationAsync(cancellationToken);
            await LockJournalChangesAsync(journalChanges, cancellationToken);
            await ValidateOrganizationChangesAsync(journalChanges, cancellationToken);
            await RecordCommittedActivityAsync(cancellationToken);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        await ProtectLocalProfileOperationAsync(cancellationToken);
        await LockJournalChangesAsync(journalChanges, cancellationToken);
        await ValidateOrganizationChangesAsync(journalChanges, cancellationToken);
        await RecordCommittedActivityAsync(cancellationToken);
        var affectedRows = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (avatarCleanup is not null)
            await avatarCleanup.DeleteUnreferencedAsync(this, obsoleteAvatarKeys);
        return affectedRows;
    }

    // Ownership applies to every journal mutation, independently of the fields supplied to AI.
    private EntityEntry[] JournalChanges() => ChangeTracker.Entries().Where(entry =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
        entry.Entity is UserProfile or UserProfileExternalConnection or Person or PersonExternalConnection or
            PersonGroup or PersonGroupMembership or PersonFact or PersonInference or Interaction or
            InteractionParticipant or InteractionAttachment or AttachmentTranscript or InteractionAnalysis or
            AnalysisConversationMessage or SuggestedProfileUpdate).ToArray();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseOpenIddict<Guid>();
        optionsBuilder.ReplaceService<IMigrationsAssembly, ProviderMigrationsAssembly>();
        optionsBuilder.AddInterceptors(SqliteSnapshotTransactions.Instance);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Explicit key bounds keep direct/design-time contexts identical to DI-created contexts.
        modelBuilder.Entity<IdentityUserLogin<Guid>>().Property(login => login.LoginProvider).HasMaxLength(128);
        modelBuilder.Entity<IdentityUserLogin<Guid>>().Property(login => login.ProviderKey).HasMaxLength(128);
        modelBuilder.Entity<IdentityUserToken<Guid>>().Property(token => token.LoginProvider).HasMaxLength(128);
        modelBuilder.Entity<IdentityUserToken<Guid>>().Property(token => token.Name).HasMaxLength(128);
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
