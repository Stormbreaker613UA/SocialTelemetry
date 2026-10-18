using SocialTelemetry.Application.Exceptions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Api.Infrastructure.Identity;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Profiles;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Infrastructure.Profiles;

public abstract class LocalProfileSelectionTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Legacy_zero_one_and_multiple_profiles_never_choose_an_ambiguous_owner()
    {
        await ResetAsync();
        await using (var empty = fixture.CreateAsyncScope())
        {
            var service = empty.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
            var selection = await service.GetAsync(Cancellation);
            Assert.Null(selection.UserProfileId);
            Assert.False(selection.LegacySingleProfileAccess);
            await AssertFailureAsync(LocalProfileFailure.SelectionRequired, () => service.ResolveAsync(null, null, Cancellation));
        }
        var first = await fixture.CreateUserProfileAsync("First");
        await RemoveSelectionAsync(); // Represents upgrade of a legacy one-profile database.
        await using (var single = fixture.CreateAsyncScope())
        {
            var service = single.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
            var resolved = await service.ResolveAsync(null, null, Cancellation);
            Assert.Equal(first, resolved.UserProfileId);
        }
        await fixture.CreateUserProfileAsync("Second");
        await RemoveSelectionAsync();
        await using var multiple = fixture.CreateAsyncScope();
        var ambiguous = multiple.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
        Assert.Null((await ambiguous.GetAsync(Cancellation)).UserProfileId);
        await AssertFailureAsync(LocalProfileFailure.SelectionRequired, () => ambiguous.ResolveAsync(null, null, Cancellation));
        Assert.Equal(2, await multiple.ServiceProvider.GetRequiredService<AppDbContext>().UserProfiles.CountAsync(Cancellation));
    }

    [Fact]
    public async Task Create_and_select_persist_without_an_account_and_require_versioned_assertions()
    {
        await ResetAsync();
        var initial = await CurrentAsync();
        Guid owner;
        await using (var create = fixture.CreateAsyncScope())
        {
            var service = create.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
            owner = (await service.CreateAsync("  Local owner  ", initial.Version, Cancellation)).Id;
        }
        Assert.Null((await CurrentAsync()).UserProfileId);
        var selected = await SelectAsync(owner, initial.Version);
        Assert.NotEqual(initial.Version, selected.Version);
        // Recreate all scoped services and the process-local cancellation registry: only SQL
        // selection survives, rather than a cached owner or cookie standing in for persistence.
        await using var restart = fixture.CreateAsyncScope();
        var database = restart.ServiceProvider.GetRequiredService<AppDbContext>();
        using var restartedLifetimes = new LocalProfileLifetimes();
        var restarted = new LocalProfileSelectionService(database, restartedLifetimes);
        var stored = await restarted.GetAsync(Cancellation);
        Assert.Equal(owner, stored.UserProfileId);
        Assert.Equal(selected.Version, stored.Version);
        Assert.False(stored.LegacySingleProfileAccess);
        Assert.False(await database.Users.AnyAsync(Cancellation));
        Assert.Equal("Local owner", (await database.UserProfiles.SingleAsync(Cancellation)).DisplayName);
        await AssertFailureAsync(LocalProfileFailure.AssertionRequired, () => restarted.ResolveAsync(null, null, Cancellation));
        await AssertFailureAsync(LocalProfileFailure.SelectionChanged, () => restarted.ResolveAsync(Guid.NewGuid(), selected.Version, Cancellation));
        await AssertFailureAsync(LocalProfileFailure.SelectionChanged, () => restarted.ResolveAsync(owner, initial.Version, Cancellation));
        Assert.Equal(owner, (await restarted.ResolveAsync(owner, selected.Version, Cancellation)).UserProfileId);
    }

    [Fact]
    public async Task Switch_cancels_original_lifetime_and_late_ignored_cancellation_cannot_save()
    {
        await ResetAsync();
        var first = await fixture.CreateUserProfileAsync("Original");
        var second = await fixture.CreateUserProfileAsync("Next");
        var initial = await CurrentAsync();
        var selected = await SelectAsync(first, initial.Version);
        await using var oldRequest = fixture.CreateAsyncScope();
        var lifetime = oldRequest.ServiceProvider.GetRequiredService<LocalProfileLifetimes>().Capture(selected.Version);
        // Deliberately omit cancellation from the bound DB operation to simulate a late provider
        // ignoring cancellation. The persisted selection guard must still reject its save.
        oldRequest.ServiceProvider.GetRequiredService<LocalProfileOperation>()
            .Bind(new LocalProfileBinding(first, selected.Version), CancellationToken.None);
        var database = oldRequest.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await database.UserProfiles.SingleAsync(profile => profile.Id == first, Cancellation);
        await SelectAsync(second, selected.Version);
        Assert.True(lifetime.IsCancellationRequested);
        original.DisplayName = "Late result";
        await AssertFailureAsync(LocalProfileFailure.SelectionChanged, () => database.SaveChangesAsync(Cancellation));
        await using var check = fixture.CreateAsyncScope();
        var stored = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Original", (await stored.UserProfiles.AsNoTracking().SingleAsync(profile => profile.Id == first, Cancellation)).DisplayName);
        Assert.Equal("Next", (await stored.UserProfiles.AsNoTracking().SingleAsync(profile => profile.Id == second, Cancellation)).DisplayName);
    }

    [Fact]
    public async Task Completed_guarded_commit_is_preserved_and_later_switch_wins_only_after_it()
    {
        await ResetAsync();
        var first = await fixture.CreateUserProfileAsync("Original");
        var second = await fixture.CreateUserProfileAsync("Next");
        var selected = await SelectAsync(first, (await CurrentAsync()).Version);
        await using var committing = fixture.CreateAsyncScope();
        committing.ServiceProvider.GetRequiredService<LocalProfileOperation>()
            .Bind(new LocalProfileBinding(first, selected.Version), CancellationToken.None);
        var database = committing.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync(Cancellation);
        await database.LockAnalysisContextAsync(first, Cancellation);
        var original = await database.UserProfiles.SingleAsync(profile => profile.Id == first, Cancellation);
        original.DisplayName = "Committed original";
        await database.SaveChangesAsync(Cancellation);
        var lockAttempt = new SelectionLockObserver();
        var switchingOptions = new DbContextOptionsBuilder<AppDbContext>(
            (DbContextOptions<AppDbContext>)database.GetService<IDbContextOptions>()).AddInterceptors(lockAttempt).Options;
        var lifetimes = committing.ServiceProvider.GetRequiredService<LocalProfileLifetimes>();
        // Microsoft.Data.Sqlite executes its asynchronous commands synchronously. A separate
        // worker is necessary to exercise the writer wait without blocking the committing thread.
        var switching = Task.Run(async () =>
        {
            await using var switchingDatabase = new AppDbContext(switchingOptions);
            return await new LocalProfileSelectionService(switchingDatabase, lifetimes).SelectAsync(second, selected.Version, Cancellation);
        }, Cancellation);
        await lockAttempt.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        Assert.False(switching.IsCompleted);
        await transaction.CommitAsync(Cancellation);
        await switching;
        await using var check = fixture.CreateAsyncScope();
        var stored = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Committed original", (await stored.UserProfiles.AsNoTracking().SingleAsync(profile => profile.Id == first, Cancellation)).DisplayName);
        Assert.Equal(second, (await CurrentAsync()).UserProfileId);
    }

    [Fact]
    public async Task Concurrent_CAS_switches_have_one_winner_and_cannot_revive_an_old_version()
    {
        await ResetAsync();
        var first = await fixture.CreateUserProfileAsync("First");
        var second = await fixture.CreateUserProfileAsync("Second");
        var initial = await CurrentAsync();
        var results = await Task.WhenAll(TrySelectAsync(first), TrySelectAsync(second));
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        var stored = await CurrentAsync();
        Assert.Contains(stored.UserProfileId, new Guid?[] { first, second });
        await AssertFailureAsync(LocalProfileFailure.SelectionChanged, () => SelectAsync(first, initial.Version));

        async Task<bool> TrySelectAsync(Guid owner)
        {
            try { await SelectAsync(owner, initial.Version); return true; }
            catch (LocalProfileAccessException exception) when (exception.Failure == LocalProfileFailure.SelectionChanged) { return false; }
        }
    }

    [Fact]
    public async Task Linked_account_is_never_an_unlock_bypass_even_when_previously_selected()
    {
        await ResetAsync();
        var owner = await fixture.CreateUserProfileAsync();
        var selection = await SelectAsync(owner, (await CurrentAsync()).Version);
        await using (var link = fixture.CreateAsyncScope())
        {
            var database = link.ServiceProvider.GetRequiredService<AppDbContext>();
            database.Users.Add(new ApplicationUser { UserProfileId = owner });
            await database.SaveChangesAsync(Cancellation);
        }
        await AssertFailureAsync(LocalProfileFailure.ProtectedAccessUnavailable, () => SelectAsync(owner, selection.Version));
        await using var request = fixture.CreateAsyncScope();
        var service = request.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
        await AssertFailureAsync(LocalProfileFailure.ProtectedAccessUnavailable, () => service.ResolveAsync(owner, selection.Version, Cancellation));
        request.ServiceProvider.GetRequiredService<LocalProfileOperation>().Bind(new LocalProfileBinding(owner, selection.Version), CancellationToken.None);
        var databaseForRequest = request.ServiceProvider.GetRequiredService<AppDbContext>();
        (await databaseForRequest.UserProfiles.SingleAsync(Cancellation)).DisplayName = "Bypass";
        await AssertFailureAsync(LocalProfileFailure.ProtectedAccessUnavailable, () => databaseForRequest.SaveChangesAsync(Cancellation));
    }

    [Fact]
    public async Task Selection_model_matches_both_migration_histories_and_enforces_singleton_and_FK()
    {
        await ResetAsync();
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(database.Database.HasPendingModelChanges());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(Cancellation));
        database.Set<LocalProfileSelection>().Add(new LocalProfileSelection { Id = 2 });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));
        database.ChangeTracker.Clear();
        database.Set<LocalProfileSelection>().Add(new LocalProfileSelection { UserProfileId = Guid.NewGuid() });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));
    }

    [Fact]
    public async Task Selection_restricts_profile_deletion_and_create_does_not_switch_or_cancel_original_work()
    {
        await ResetAsync();
        var first = await fixture.CreateUserProfileAsync("Original");
        var selected = await SelectAsync(first, (await CurrentAsync()).Version);
        await using var scope = fixture.CreateAsyncScope();
        var lifetimes = scope.ServiceProvider.GetRequiredService<LocalProfileLifetimes>();
        var lifetime = lifetimes.Capture(selected.Version);
        var service = scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
        var created = await service.CreateAsync("Another profile", selected.Version, Cancellation);
        Assert.NotEqual(first, created.Id);
        Assert.False(lifetime.IsCancellationRequested);
        var stored = await CurrentAsync();
        Assert.Equal(first, stored.UserProfileId);
        Assert.Equal(selected.Version, stored.Version);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.UserProfiles.Remove(await database.UserProfiles.SingleAsync(profile => profile.Id == first, Cancellation));
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));
    }

    [Fact]
    public async Task Prefeature_upgrade_preserves_existing_graph_and_only_adopts_unambiguous_profile()
    {
        await ResetAsync();
        var profileId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var interactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var transcriptVersion = Guid.NewGuid();
        Guid storeId;
        await using (var legacy = fixture.CreateAsyncScope())
        {
            var database = legacy.ServiceProvider.GetRequiredService<AppDbContext>();
            var previous = database.Database.GetMigrations().Single(migration => migration.EndsWith("AddAccountFoundation", StringComparison.Ordinal));
            // Only this test fixture's disposable database is downgraded to the published baseline.
            await database.GetService<IMigrator>().MigrateAsync(previous, Cancellation);
            storeId = (await database.StorageDatabaseIdentities.SingleAsync(Cancellation)).StoreId;
            database.UserProfiles.Add(new UserProfile { Id = profileId, DisplayName = "Existing owner" });
            database.People.Add(new Person { Id = personId, UserProfileId = profileId, DisplayName = "Existing person" });
            database.Interactions.Add(new Interaction { Id = interactionId, UserProfileId = profileId, Title = "Existing journal" });
            database.InteractionParticipants.Add(new() { InteractionId = interactionId, PersonId = personId });
            database.InteractionAttachments.Add(new()
            {
                Id = attachmentId, InteractionId = interactionId, Type = AttachmentType.Audio,
                Status = AttachmentStatus.Ready, StorageKey = new string('a', 32), MimeType = "audio/wav"
            });
            database.AttachmentTranscripts.Add(new()
            {
                AttachmentId = attachmentId, CorrectedText = "Existing reviewed correction", Version = transcriptVersion,
                ReviewStatus = TranscriptReviewStatus.Reviewed, ReviewedAt = DateTimeOffset.UtcNow,
                SourceStorageKey = new string('a', 32), SourceSha256 = new string('B', 64)
            });
            await database.SaveChangesAsync(Cancellation);
        }
        await using (var upgrade = fixture.CreateAsyncScope())
        {
            var database = upgrade.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.Database.MigrateAsync(Cancellation);
            Assert.Equal(profileId, (await database.UserProfiles.SingleAsync(Cancellation)).Id);
            Assert.Equal(personId, (await database.People.SingleAsync(Cancellation)).Id);
            Assert.Equal(interactionId, (await database.Interactions.SingleAsync(Cancellation)).Id);
            Assert.Equal(attachmentId, (await database.InteractionAttachments.SingleAsync(Cancellation)).Id);
            var transcript = await database.AttachmentTranscripts.SingleAsync(Cancellation);
            Assert.Equal(transcriptVersion, transcript.Version);
            Assert.Equal("Existing reviewed correction", transcript.CorrectedText);
            Assert.Equal(TranscriptReviewStatus.Reviewed, transcript.ReviewStatus);
            Assert.Equal(storeId, (await database.StorageDatabaseIdentities.SingleAsync(Cancellation)).StoreId);
            Assert.False(await database.Users.AnyAsync(Cancellation));
            Assert.False(await database.Set<LocalProfileSelection>().AnyAsync(Cancellation));
            Assert.Equal(profileId, (await upgrade.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation)).UserProfileId);
        }
    }

    [Fact]
    public async Task Rejected_switch_does_not_cancel_and_callback_failure_cannot_undo_a_committed_switch()
    {
        await ResetAsync();
        var first = await fixture.CreateUserProfileAsync("First");
        var second = await fixture.CreateUserProfileAsync("Second");
        var selected = await SelectAsync(first, (await CurrentAsync()).Version);
        await using var scope = fixture.CreateAsyncScope();
        var lifetimes = scope.ServiceProvider.GetRequiredService<LocalProfileLifetimes>();
        var lifetime = lifetimes.Capture(selected.Version);
        await Assert.ThrowsAsync<SocialTelemetry.Application.Exceptions.NotFoundException>(() => SelectAsync(Guid.NewGuid(), selected.Version));
        Assert.False(lifetime.IsCancellationRequested);
        Assert.Equal(first, (await CurrentAsync()).UserProfileId);
        using var callback = lifetime.Register(() => throw new InvalidOperationException("Synthetic cancellation callback failure"));
        var next = await SelectAsync(second, selected.Version);
        Assert.True(lifetime.IsCancellationRequested);
        Assert.Equal(next.Version, (await CurrentAsync()).Version);
        Assert.False(lifetimes.Capture(next.Version).IsCancellationRequested);
    }

    [Fact]
    public async Task Immutable_binding_rejects_foreign_context_writes_without_retargeting_the_operation()
    {
        await ResetAsync();
        var first = await fixture.CreateUserProfileAsync("First");
        var second = await fixture.CreateUserProfileAsync("Second");
        Guid personId;
        await using (var seed = fixture.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var person = new Person { Id = Guid.NewGuid(), UserProfileId = second, DisplayName = "Foreign person" };
            database.People.Add(person);
            await database.SaveChangesAsync(Cancellation);
            personId = person.Id;
        }
        var selected = await SelectAsync(first, (await CurrentAsync()).Version);
        await using (var request = fixture.CreateAsyncScope())
        {
            var operation = request.ServiceProvider.GetRequiredService<LocalProfileOperation>();
            operation.Bind(new LocalProfileBinding(first, selected.Version), CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => operation.Bind(new LocalProfileBinding(second, selected.Version), CancellationToken.None));
            Assert.Equal(first, operation.RequireOwner());
            var database = request.ServiceProvider.GetRequiredService<AppDbContext>();
            (await database.People.SingleAsync(person => person.Id == personId, Cancellation)).DisplayName = "Forbidden change";
            await Assert.ThrowsAsync<SocialTelemetry.Application.Exceptions.NotFoundException>(() => database.SaveChangesAsync(Cancellation));
        }
        await using var check = fixture.CreateAsyncScope();
        Assert.Equal("Foreign person", (await check.ServiceProvider.GetRequiredService<AppDbContext>().People.SingleAsync(person => person.Id == personId, Cancellation)).DisplayName);
    }

    private async Task ResetAsync()
    {
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Set<LocalProfileSelection>().ExecuteDeleteAsync(Cancellation);
        await database.Users.ExecuteDeleteAsync(Cancellation);
        await database.UserProfiles.ExecuteDeleteAsync(Cancellation);
    }

    private async Task RemoveSelectionAsync()
    {
        await using var scope = fixture.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<LocalProfileSelection>().ExecuteDeleteAsync(Cancellation);
    }

    private async Task<LocalProfileSelection> CurrentAsync()
    {
        await using var scope = fixture.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
    }

    private async Task<LocalProfileSelection> SelectAsync(Guid owner, Guid expectedVersion)
    {
        await using var scope = fixture.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().SelectAsync(owner, expectedVersion, Cancellation);
    }

    private static async Task AssertFailureAsync(LocalProfileFailure failure, Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<LocalProfileAccessException>(operation);
        Assert.Equal(failure, exception.Failure);
    }

    private sealed class SelectionLockObserver : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            // SQLite takes the writer reservation at BEGIN IMMEDIATE, before the guard command.
            if (connection is Microsoft.Data.Sqlite.SqliteConnection) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"LocalProfileSelection\"", StringComparison.Ordinal))
                Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class LocalProfileSelectionTests(PeopleApiFixture fixture) : LocalProfileSelectionTestsContract<PeopleApiFixture>(fixture);

[Collection("SQLite")]
public sealed class SqliteLocalProfileSelectionTests(SqliteApiFixture fixture) : LocalProfileSelectionTestsContract<SqliteApiFixture>(fixture);
