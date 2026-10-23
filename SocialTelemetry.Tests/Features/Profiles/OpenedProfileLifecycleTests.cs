using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.Sqlite;
using Npgsql;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.LocalAccess;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Tests.Features.People;
using DeleteProfile = SocialTelemetry.Application.Features.LocalProfiles.Delete.Scenario;
using RenameProfile = SocialTelemetry.Application.Features.LocalProfiles.Rename.Scenario;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class OpenedProfileLifecycleTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private const string Password = "Synthetic-LifecycleDelete9!";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Host(Action<IServiceCollection>? configure = null, string? credentials = null) =>
        fixture.WithServices(services =>
        {
            services.Configure<ChatGptOptions>(options => options.DataDirectory = credentials ??
                Path.GetFullPath(Path.Combine(fixture.AttachmentStorageDirectory, "..", "lifecycle-credentials")));
            services.AddSingleton<IStartupFilter>(new LoopbackFilter());
            configure?.Invoke(services);
        });

    [Fact]
    public async Task Rename_changes_only_display_name_and_keeps_login_security_and_all_profile_fields()
    {
        var owner = await fixture.CreateUserProfileAsync();
        await fixture.CreateUserProfileAsync("Changed display", select: false);
        using var host = Host();
        var login = "rename-" + Guid.NewGuid().ToString("N");
        await using (var seed = host.Services.CreateAsyncScope())
        {
            await ResolveAsync(seed.ServiceProvider, owner);
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await database.UserProfiles.SingleAsync(row => row.Id == owner, Cancellation);
            profile.AboutMe = "About"; profile.Goals = "Goals"; profile.AiInstructions = "Preferences";
            await database.SaveChangesAsync(Cancellation);
            await seed.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        }
        var signed = await SignInAsync(host.Services, owner, login);
        await using var active = host.Services.CreateAsyncScope();
        active.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        var version = await ResolveAsync(active.ServiceProvider, owner);
        var before = await active.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .SingleAsync(account => account.UserProfileId == owner, Cancellation);
        var scenario = active.ServiceProvider.GetRequiredService<RenameProfile>();
        await Assert.ThrowsAsync<InvalidInputException>(() => scenario.ExecuteAsync(owner, "  ", version, Cancellation));
        await Assert.ThrowsAsync<NotFoundException>(() => scenario.ExecuteAsync(Guid.NewGuid(), "Foreign", version, Cancellation));
        var renamed = await scenario.ExecuteAsync(owner, "  Changed display  ", version, Cancellation);
        Assert.Equal("Changed display", renamed.DisplayName);
        Assert.Equal(version, renamed.Version);
        await using var checking = host.Services.CreateAsyncScope();
        var databaseAfter = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await databaseAfter.Users.AsNoTracking().SingleAsync(account => account.UserProfileId == owner, Cancellation);
        Assert.Equal(before.Id, after.Id); Assert.Equal(login, after.UserName);
        Assert.True(before.PasswordHash == after.PasswordHash); Assert.Equal(before.AccessEpoch, after.AccessEpoch);
        Assert.True(before.RecoveryHash == after.RecoveryHash);
        var stored = await databaseAfter.UserProfiles.AsNoTracking().SingleAsync(profile => profile.Id == owner, Cancellation);
        Assert.Equal("About", stored.AboutMe); Assert.Equal("Goals", stored.Goals); Assert.Equal("Preferences", stored.AiInstructions);
        // Existing full-update name semantics: nonunique, and no new 200-character restriction.
        var longName = new string('n', 201);
        await scenario.ExecuteAsync(owner, longName, version, Cancellation);
        await scenario.ExecuteAsync(owner, "Changed display", version, Cancellation);
    }

    [Fact]
    public async Task Deletion_requires_real_admission_confirmation_exact_target_and_version_without_side_effects()
    {
        var owner = await fixture.CreateUserProfileAsync();
        using var host = Host();
        await using var forged = host.Services.CreateAsyncScope();
        var selector = forged.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
        var selection = await selector.GetAsync(Cancellation);
        forged.ServiceProvider.GetRequiredService<LocalProfileOperation>().Bind(new(owner, selection.Version), CancellationToken.None);
        await Assert.ThrowsAsync<LocalAccessException>(() => forged.ServiceProvider.GetRequiredService<DeleteProfile>()
            .ExecuteAsync(owner, selection.Version, true, null, Cancellation));
        await using var active = host.Services.CreateAsyncScope();
        var version = await ResolveAsync(active.ServiceProvider, owner);
        var scenario = active.ServiceProvider.GetRequiredService<DeleteProfile>();
        await Assert.ThrowsAsync<InvalidInputException>(() => scenario.ExecuteAsync(owner, version, false, null, Cancellation));
        await Assert.ThrowsAsync<NotFoundException>(() => scenario.ExecuteAsync(Guid.NewGuid(), version, true, null, Cancellation));
        var stale = await Assert.ThrowsAsync<LocalProfileAccessException>(() => scenario.ExecuteAsync(owner, Guid.NewGuid(), true, null, Cancellation));
        Assert.Equal(LocalProfileFailure.SelectionChanged, stale.Failure);
        Assert.True(await active.ServiceProvider.GetRequiredService<AppDbContext>().UserProfiles.AnyAsync(profile => profile.Id == owner, Cancellation));
    }

    [Fact]
    public async Task Delete_cascades_account_graph_but_preserves_other_profiles_grants_shared_files_and_device_ticket()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        using var host = Host();
        var login = "delete-" + Guid.NewGuid().ToString("N");
        await using (var enrollment = host.Services.CreateAsyncScope())
        {
            await ResolveAsync(enrollment.ServiceProvider, owner);
            await enrollment.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        }
        var signed = await SignInAsync(host.Services, owner, login);
        Guid deletedAccount;
        Guid retainedAccount;
        Guid device;
        Guid removedPerson = Guid.Empty;
        Guid removedInteraction = Guid.Empty;
        Guid removedAttachment = Guid.Empty;
        Guid removedAnalysis = Guid.Empty;
        string key;
        await using (var seed = host.Services.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            deletedAccount = await database.Users.Where(account => account.UserProfileId == owner).Select(account => account.Id).SingleAsync(Cancellation);
            device = await database.Set<LocalProfileGrant>().Where(grant => grant.AccountId == deletedAccount).Select(grant => grant.DeviceId).SingleAsync(Cancellation);
            var retained = new ApplicationUser { UserProfileId = other, UserName = "retained-" + Guid.NewGuid(), LocalProtection = LocalProtectionStatus.Disabled };
            database.Add(retained);
            await database.SaveChangesAsync(Cancellation);
            retainedAccount = retained.Id;
            database.Add(new LocalProfileGrant { AccountId = retained.Id, DeviceId = device, SecurityStamp = "synthetic", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            database.Add(new IdentityUserClaim<Guid> { UserId = deletedAccount, ClaimType = "synthetic", ClaimValue = "delete-with-account" });
            database.Add(new IdentityUserToken<Guid> { UserId = deletedAccount, LoginProvider = "synthetic", Name = "synthetic", Value = "synthetic" });
            database.Add(new ProfileAiSelection { UserProfileId = owner });
            var storage = seed.ServiceProvider.GetRequiredService<IAttachmentStorage>();
            using var bytes = new MemoryStream([1, 2, 3]);
            key = await storage.SaveAsync(bytes, "synthetic.wav", "audio/wav", Cancellation);
            await storage.CompleteUploadAsync(key, Cancellation);
            foreach (var profile in new[] { owner, other })
            {
                var person = new Person { Id = Guid.NewGuid(), UserProfileId = profile, DisplayName = "Synthetic" };
                var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = profile, Title = "Synthetic", OccurredAt = DateTimeOffset.UtcNow };
                database.People.Add(person); database.Interactions.Add(interaction);
                database.PersonFacts.Add(new() { Id = Guid.NewGuid(), PersonId = person.Id, Value = "Synthetic fact" });
                database.InteractionParticipants.Add(new() { PersonId = person.Id, InteractionId = interaction.Id });
                var attachment = new InteractionAttachment { Id = Guid.NewGuid(), InteractionId = interaction.Id, StorageKey = key,
                    Type = AttachmentType.Audio, Status = profile == other ? AttachmentStatus.Pending : AttachmentStatus.Ready };
                database.Add(attachment);
                database.AttachmentTranscripts.Add(new() { AttachmentId = attachment.Id, CorrectedText = "Reviewed synthetic words",
                    Version = Guid.NewGuid(), ReviewStatus = TranscriptReviewStatus.Reviewed, SourceStorageKey = key, ReviewedAt = DateTimeOffset.UtcNow });
                database.PersonInferences.Add(new() { Id = Guid.NewGuid(), PersonId = person.Id, SourceInteractionId = interaction.Id, Value = "Synthetic inference" });
                var analysis = new InteractionAnalysis { Id = Guid.NewGuid(), InteractionId = interaction.Id, Summary = "Synthetic historical analysis" };
                database.Add(analysis);
                database.AnalysisConversationMessages.Add(new() { Id = Guid.NewGuid(), InteractionAnalysisId = analysis.Id, Role = "user", Content = "Synthetic history" });
                database.SuggestedProfileUpdates.Add(new() { Id = Guid.NewGuid(), PersonId = person.Id, InteractionAnalysisId = analysis.Id, Field = "Notes", SuggestedValue = "Synthetic pending" });
                database.PersonExternalConnections.Add(new() { Id = Guid.NewGuid(), PersonId = person.Id, Platform = "example", Handle = "synthetic" });
                database.UserProfileExternalConnections.Add(new() { Id = Guid.NewGuid(), UserProfileId = profile, Platform = "example", Handle = "synthetic" });
                if (profile == owner)
                {
                    removedPerson = person.Id; removedInteraction = interaction.Id;
                    removedAttachment = attachment.Id; removedAnalysis = analysis.Id;
                }
            }
            await database.SaveChangesAsync(Cancellation);
        }
        await using var active = host.Services.CreateAsyncScope();
        active.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        var version = await ResolveAsync(active.ServiceProvider, owner);
        var scenario = active.ServiceProvider.GetRequiredService<DeleteProfile>();
        var wrong = await Assert.ThrowsAsync<LocalAccessException>(() => scenario.ExecuteAsync(owner, version, true, "Wrong-Synthetic9!", Cancellation));
        Assert.Equal(LocalAccessFailure.InvalidCredentials, wrong.Failure);
        var deleted = await scenario.ExecuteAsync(owner, version, true, Password, Cancellation);
        Assert.Null(deleted.UserProfileId);
        Assert.NotEqual(version, deleted.Version);
        Assert.True(active.ServiceProvider.GetRequiredService<LocalProfileOperation>().Lifetime.IsCancellationRequested);
        await using var checking = host.Services.CreateAsyncScope();
        var stored = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await stored.UserProfiles.AnyAsync(profile => profile.Id == owner, Cancellation));
        Assert.False(await stored.Users.AnyAsync(account => account.Id == deletedAccount, Cancellation));
        Assert.False(await stored.Set<LocalProfileGrant>().AnyAsync(grant => grant.AccountId == deletedAccount, Cancellation));
        Assert.False(await stored.Set<ProfileAiSelection>().AnyAsync(row => row.UserProfileId == owner, Cancellation));
        Assert.True(await stored.Users.AnyAsync(account => account.Id == retainedAccount, Cancellation));
        Assert.True(await stored.Set<LocalProfileGrant>().AnyAsync(grant => grant.AccountId == retainedAccount, Cancellation));
        Assert.True(await stored.Set<LocalDeviceTicket>().AnyAsync(row => row.Id == device, Cancellation));
        Assert.True(await stored.People.AnyAsync(person => person.UserProfileId == other, Cancellation));
        Assert.False(await stored.People.AnyAsync(person => person.UserProfileId == owner, Cancellation));
        Assert.False(await stored.PersonFacts.AnyAsync(row => row.PersonId == removedPerson, Cancellation));
        Assert.False(await stored.PersonInferences.AnyAsync(row => row.PersonId == removedPerson, Cancellation));
        Assert.False(await stored.Interactions.AnyAsync(row => row.Id == removedInteraction, Cancellation));
        Assert.False(await stored.InteractionParticipants.AnyAsync(row => row.InteractionId == removedInteraction, Cancellation));
        Assert.False(await stored.InteractionAttachments.AnyAsync(row => row.Id == removedAttachment, Cancellation));
        Assert.False(await stored.AttachmentTranscripts.AnyAsync(row => row.AttachmentId == removedAttachment, Cancellation));
        Assert.False(await stored.InteractionAnalyses.AnyAsync(row => row.Id == removedAnalysis, Cancellation));
        Assert.False(await stored.AnalysisConversationMessages.AnyAsync(row => row.InteractionAnalysisId == removedAnalysis, Cancellation));
        Assert.False(await stored.SuggestedProfileUpdates.AnyAsync(row => row.InteractionAnalysisId == removedAnalysis, Cancellation));
        Assert.False(await stored.PersonExternalConnections.AnyAsync(row => row.PersonId == removedPerson, Cancellation));
        Assert.False(await stored.UserProfileExternalConnections.AnyAsync(row => row.UserProfileId == owner, Cancellation));
        Assert.False(await stored.Set<IdentityUserClaim<Guid>>().AnyAsync(row => row.UserId == deletedAccount, Cancellation));
        Assert.False(await stored.Set<IdentityUserToken<Guid>>().AnyAsync(row => row.UserId == deletedAccount, Cancellation));
        Assert.True(await stored.InteractionAnalyses.AnyAsync(row => row.Interaction.UserProfileId == other, Cancellation));
        Assert.True(await stored.SuggestedProfileUpdates.AnyAsync(row => row.Person.UserProfileId == other, Cancellation));
        Assert.True(await stored.UserProfileExternalConnections.AnyAsync(row => row.UserProfileId == other, Cancellation));
        Assert.True(await checking.ServiceProvider.GetRequiredService<IAttachmentStorage>().ExistsAsync(key, Cancellation));
    }

    [Theory]
    [InlineData("participant")]
    [InlineData("inference")]
    [InlineData("suggestion")]
    public async Task Legacy_foreign_cascades_are_rejected_without_modifying_either_journal(string relation)
    {
        var owner = await fixture.CreateUserProfileAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        using var host = Host();
        await using (var seed = host.Services.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var person = new Person { Id = Guid.NewGuid(), UserProfileId = other, DisplayName = "Retained" };
            var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = owner, Title = "Synthetic" };
            database.AddRange(person, interaction);
            if (relation == "participant") database.Add(new InteractionParticipant { Person = person, Interaction = interaction });
            else if (relation == "inference") database.Add(new PersonInference { Id = Guid.NewGuid(), Person = person, SourceInteraction = interaction, Value = "Synthetic" });
            else
            {
                var analysis = new InteractionAnalysis { Id = Guid.NewGuid(), Interaction = interaction, Summary = "Synthetic" };
                database.Add(new SuggestedProfileUpdate { Id = Guid.NewGuid(), Person = person, InteractionAnalysis = analysis, Field = "Notes", SuggestedValue = "Synthetic" });
            }
            await database.SaveChangesAsync(Cancellation);
        }
        await using var active = host.Services.CreateAsyncScope();
        var version = await ResolveAsync(active.ServiceProvider, owner);
        await Assert.ThrowsAsync<ConflictException>(() => active.ServiceProvider.GetRequiredService<DeleteProfile>()
            .ExecuteAsync(owner, version, true, null, Cancellation));
        Assert.False(active.ServiceProvider.GetRequiredService<LocalProfileOperation>().Lifetime.IsCancellationRequested);
        await using var checking = host.Services.CreateAsyncScope();
        Assert.Equal(2, await checking.ServiceProvider.GetRequiredService<AppDbContext>().UserProfiles.CountAsync(profile => profile.Id == owner || profile.Id == other, Cancellation));
    }

    [Fact]
    public async Task Expired_or_wrong_device_cannot_delete_even_with_the_correct_password()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var clock = new LocalAccessClock();
        using var host = Host(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            // Isolate authoritative deadline rejection from the separately covered timer
            // cancellation, so its exception/order is deterministic in this direct test.
            foreach (var descriptor in services.Where(service => service.ImplementationType == typeof(LocalAccessExpirationService)).ToArray())
                services.Remove(descriptor);
        });
        var login = "delete-expiry-" + Guid.NewGuid().ToString("N");
        await using (var enrollment = host.Services.CreateAsyncScope())
        {
            await ResolveAsync(enrollment.ServiceProvider, owner);
            await enrollment.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        }
        var signed = await SignInAsync(host.Services, owner, login);
        await using (var foreign = host.Services.CreateAsyncScope())
        {
            foreign.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(new(new string('A', 64)));
            var denied = await Assert.ThrowsAsync<LocalAccessException>(() => ResolveAsync(foreign.ServiceProvider, owner));
            Assert.Equal(LocalAccessFailure.Required, denied.Failure);
        }
        await using var active = host.Services.CreateAsyncScope();
        active.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        var version = await ResolveAsync(active.ServiceProvider, owner);
        clock.Advance(TimeSpan.FromDays(14));
        var expired = await Assert.ThrowsAsync<LocalAccessException>(() => active.ServiceProvider.GetRequiredService<DeleteProfile>()
            .ExecuteAsync(owner, version, true, Password, Cancellation));
        Assert.Equal(LocalAccessFailure.Required, expired.Failure);
        await using var checking = host.Services.CreateAsyncScope();
        Assert.True(await checking.ServiceProvider.GetRequiredService<AppDbContext>().UserProfiles.AnyAsync(profile => profile.Id == owner, Cancellation));
    }

    [Fact]
    public async Task Other_host_switch_winning_before_delete_protection_rejects_the_stale_operation_without_retargeting()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        var gate = new AdmissionReadGate();
        using var host = Host(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate));
            foreach (var descriptor in services.Where(service => service.ImplementationType == typeof(LocalAccessExpirationService)).ToArray())
                services.Remove(descriptor);
        });
        using var otherHost = Host();
        using var otherClient = otherHost.CreateClient();
        await using var active = host.Services.CreateAsyncScope();
        var version = await ResolveAsync(active.ServiceProvider, owner);
        gate.Armed = true;
        var deleting = active.ServiceProvider.GetRequiredService<DeleteProfile>().ExecuteAsync(owner, version, true, null, Cancellation);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        try
        {
            await using var switching = otherHost.Services.CreateAsyncScope();
            await switching.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().SelectAsync(other, version, Cancellation);
            Assert.False(active.ServiceProvider.GetRequiredService<LocalProfileOperation>().Lifetime.IsCancellationRequested);
        }
        finally { gate.Continue.TrySetResult(); }
        var rejected = await Assert.ThrowsAsync<LocalProfileAccessException>(() => deleting);
        Assert.Equal(LocalProfileFailure.SelectionChanged, rejected.Failure);
        await using var checking = otherHost.Services.CreateAsyncScope();
        var database = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await database.UserProfiles.AnyAsync(profile => profile.Id == owner, Cancellation));
        Assert.True(await database.UserProfiles.AnyAsync(profile => profile.Id == other, Cancellation));
        Assert.Equal(other, (await database.Set<LocalProfileSelection>().AsNoTracking().SingleAsync(Cancellation)).UserProfileId);
    }

    [Fact]
    public async Task Pending_caller_work_is_refused_without_being_saved_or_discarded()
    {
        var owner = await fixture.CreateUserProfileAsync();
        using var host = Host();
        await using var active = host.Services.CreateAsyncScope();
        var version = await ResolveAsync(active.ServiceProvider, owner);
        var database = active.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = new Person { Id = Guid.NewGuid(), UserProfileId = owner, DisplayName = "Unsaved" };
        database.Add(pending);
        await Assert.ThrowsAsync<ConflictException>(() => active.ServiceProvider.GetRequiredService<DeleteProfile>().ExecuteAsync(owner, version, true, null, Cancellation));
        Assert.Equal(EntityState.Added, database.Entry(pending).State);
        await using var checking = host.Services.CreateAsyncScope();
        Assert.False(await checking.ServiceProvider.GetRequiredService<AppDbContext>().People.AnyAsync(person => person.Id == pending.Id, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_or_commit_failure_rolls_back_profile_account_selection_and_cleanup_receipt(bool commit)
    {
        var owner = await fixture.CreateUserProfileAsync();
        var fault = new DeletionFailure(commit);
        using var host = Host(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(fault)));
        var login = "rollback-delete-" + Guid.NewGuid().ToString("N");
        await using (var enrollment = host.Services.CreateAsyncScope())
        {
            await ResolveAsync(enrollment.ServiceProvider, owner);
            await enrollment.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        }
        var signed = await SignInAsync(host.Services, owner, login);
        await using (var seed = host.Services.CreateAsyncScope())
        {
            seed.ServiceProvider.GetRequiredService<AppDbContext>().Add(new ProfileAiSelection { UserProfileId = owner });
            await seed.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(Cancellation);
        }
        await using var active = host.Services.CreateAsyncScope();
        active.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        var version = await ResolveAsync(active.ServiceProvider, owner);
        fault.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => active.ServiceProvider.GetRequiredService<DeleteProfile>()
            .ExecuteAsync(owner, version, true, Password, Cancellation));
        await using var checking = host.Services.CreateAsyncScope();
        var database = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await database.UserProfiles.AnyAsync(profile => profile.Id == owner, Cancellation));
        var selection = await database.Set<LocalProfileSelection>().AsNoTracking().SingleAsync(Cancellation);
        Assert.Equal(owner, selection.UserProfileId); Assert.Equal(version, selection.Version);
        Assert.False(await database.Set<ProfileDeletionCleanupMarker>().AnyAsync(marker => marker.UserProfileId == owner, Cancellation));
        var account = await database.Users.AsNoTracking().SingleAsync(row => row.UserProfileId == owner, Cancellation);
        Assert.Equal(LocalProtectionStatus.Enabled, account.LocalProtection);
        Assert.True(await database.Set<LocalProfileGrant>().AnyAsync(grant => grant.AccountId == account.Id && !grant.Revoked, Cancellation));
        Assert.True(await database.Set<ProfileAiSelection>().AnyAsync(row => row.UserProfileId == owner, Cancellation));
        Assert.False(active.ServiceProvider.GetRequiredService<LocalProfileOperation>().Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task Failed_physical_media_cleanup_returns_committed_success_and_aged_orphan_is_recovered_on_restart()
    {
        var owner = await fixture.CreateUserProfileAsync();
        await using var backing = fixture.CreateAsyncScope();
        var storage = backing.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        var failed = new FailedDeleteStorage(storage);
        using var host = Host(services =>
        {
            services.RemoveAll<IAttachmentStorage>();
            services.AddSingleton<IAttachmentStorage>(failed);
        });
        using var bytes = new MemoryStream([1, 2, 3]);
        var key = await storage.SaveAsync(bytes, "synthetic.wav", "audio/wav", Cancellation);
        await storage.CompleteUploadAsync(key, Cancellation);
        await using (var seed = fixture.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = owner, Title = "Synthetic cleanup" };
            database.Add(interaction);
            database.Add(new InteractionAttachment { Id = Guid.NewGuid(), InteractionId = interaction.Id, Type = AttachmentType.Audio,
                Status = AttachmentStatus.Ready, StorageKey = key });
            await database.SaveChangesAsync(Cancellation);
        }
        await using (var active = host.Services.CreateAsyncScope())
        {
            var version = await ResolveAsync(active.ServiceProvider, owner);
            failed.Armed = true;
            var result = await active.ServiceProvider.GetRequiredService<DeleteProfile>().ExecuteAsync(owner, version, true, null, Cancellation);
            Assert.True(result.CleanupPending); Assert.Null(result.UserProfileId);
        }
        Assert.True(await storage.ExistsAsync(key, Cancellation));
        // Exact generated fixture key, not an application/default media path.
        File.SetLastWriteTimeUtc(Path.Combine(fixture.AttachmentStorageDirectory, key), DateTime.UtcNow.AddDays(-1));
        using var restarted = Host();
        using var restartedClient = restarted.CreateClient();
        Assert.False(await storage.ExistsAsync(key, Cancellation));
        await using var checking = restarted.Services.CreateAsyncScope();
        Assert.False(await checking.ServiceProvider.GetRequiredService<AppDbContext>().UserProfiles.AnyAsync(profile => profile.Id == owner, Cancellation));
    }

    [Fact]
    public async Task Addressed_credential_cleanup_failure_keeps_receipt_and_restart_removes_only_deleted_owners_entries()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var unrelatedOwner = Guid.NewGuid(); // May belong to another database sharing this credential directory.
        var directory = Path.GetFullPath(Path.Combine(fixture.AttachmentStorageDirectory, "..", "cleanup-" + Guid.NewGuid().ToString("N")));
        using var host = Host(credentials: directory);
        var store = host.Services.GetRequiredService<ChatGptCredentialStore>();
        using (await store.LockAsync(Cancellation))
        {
            var state = await store.ReadAsync(Cancellation);
            state.Accounts.Add(new ChatGptAccount { UserProfileId = owner, ClientId = "synthetic-deleted", Tokens = new() { AccessToken = "synthetic-secret" } });
            state.Accounts.Add(new ChatGptAccount { UserProfileId = unrelatedOwner, ClientId = "synthetic-retained", Tokens = new() { AccessToken = "synthetic-other" } });
            state.ActiveConnectionId = state.Accounts[0].Id;
            await store.SaveAsync(state, Cancellation);
        }
        var file = Path.Combine(directory, "chatgpt-state.bin");
        var encrypted = await File.ReadAllBytesAsync(file, Cancellation);
        await File.WriteAllBytesAsync(file, [1, 2, 3], Cancellation);
        await using (var active = host.Services.CreateAsyncScope())
        {
            var version = await ResolveAsync(active.ServiceProvider, owner);
            var result = await active.ServiceProvider.GetRequiredService<DeleteProfile>().ExecuteAsync(owner, version, true, null, Cancellation);
            Assert.True(result.CleanupPending);
        }
        await using (var checking = host.Services.CreateAsyncScope())
            Assert.True(await checking.ServiceProvider.GetRequiredService<AppDbContext>().Set<ProfileDeletionCleanupMarker>()
                .AnyAsync(marker => marker.UserProfileId == owner, Cancellation));
        await File.WriteAllBytesAsync(file, encrypted, Cancellation);
        using var restarted = Host(credentials: directory);
        using var client = restarted.CreateClient(); // Startup reconciliation retries only the durable receipt.
        var restartedStore = restarted.Services.GetRequiredService<ChatGptCredentialStore>();
        using (await restartedStore.LockAsync(Cancellation))
        {
            var after = await restartedStore.ReadAsync(Cancellation);
            var retained = Assert.Single(after.Accounts);
            Assert.Equal(unrelatedOwner, retained.UserProfileId);
            Assert.Equal("synthetic-other", retained.Tokens?.AccessToken);
            Assert.Null(after.ActiveConnectionId);
        }
        await using var final = restarted.Services.CreateAsyncScope();
        var cleanup = final.ServiceProvider.GetRequiredService<ProfileDeletionCleanup>();
        Assert.True(await cleanup.TryCleanAsync(owner, Cancellation)); // Idempotent after receipt removal.
        Assert.False(await final.ServiceProvider.GetRequiredService<AppDbContext>().Set<ProfileDeletionCleanupMarker>()
            .AnyAsync(marker => marker.UserProfileId == owner, Cancellation));
    }

    [Fact]
    public async Task Retained_deletion_scope_observes_another_hosts_password_lockout_without_new_grants_or_deletion()
    {
        var owner = await fixture.CreateUserProfileAsync();
        using var host = Host();
        using var otherHost = Host();
        var login = "delete-lockout-" + Guid.NewGuid().ToString("N");
        await using (var enrollment = host.Services.CreateAsyncScope())
        {
            await ResolveAsync(enrollment.ServiceProvider, owner);
            await enrollment.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        }
        var signed = await SignInAsync(host.Services, owner, login);
        await using var retained = host.Services.CreateAsyncScope();
        retained.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        var version = await ResolveAsync(retained.ServiceProvider, owner);
        var deletion = retained.ServiceProvider.GetRequiredService<DeleteProfile>();
        var first = await Assert.ThrowsAsync<LocalAccessException>(() => deletion.ExecuteAsync(owner, version, true, "Wrong-Synthetic9!", Cancellation));
        Assert.Equal(LocalAccessFailure.InvalidCredentials, first.Failure);
        for (var attempt = 2; attempt <= 5; attempt++)
        {
            await using var external = otherHost.Services.CreateAsyncScope();
            var failed = await Assert.ThrowsAsync<LocalAccessException>(() => external.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .SignInAsync(new(login, "Wrong-Synthetic9!", owner, version, true), Cancellation));
            Assert.Equal(attempt == 5 ? LocalAccessFailure.LockedOut : LocalAccessFailure.InvalidCredentials, failed.Failure);
        }
        await using var checking = otherHost.Services.CreateAsyncScope();
        var database = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await database.Users.AsNoTracking().SingleAsync(account => account.UserProfileId == owner, Cancellation);
        foreach (var supplied in new[] { Password, "Wrong-Synthetic9!" })
        {
            var denied = await Assert.ThrowsAsync<LocalAccessException>(() => deletion.ExecuteAsync(owner, version, true, supplied, Cancellation));
            Assert.Equal(LocalAccessFailure.LockedOut, denied.Failure);
        }
        var after = await database.Users.AsNoTracking().SingleAsync(account => account.Id == before.Id, Cancellation);
        Assert.Equal(before.LockoutEnd, after.LockoutEnd); Assert.Equal(before.AccessFailedCount, after.AccessFailedCount);
        Assert.True(before.RecoveryHash == after.RecoveryHash); Assert.True(before.PasswordHash == after.PasswordHash);
        Assert.True(await database.UserProfiles.AnyAsync(profile => profile.Id == owner, Cancellation));
    }

    [Fact]
    public async Task Cleanup_migration_upgrades_the_published_schema_preserving_ids_and_journal_and_matches_current_model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        var directory = Directory.CreateTempSubdirectory("SocialTelemetry.Lifecycle.Upgrade.");
        var schema = "lifecycle_" + Guid.NewGuid().ToString("N");
        await using var fixtureScope = fixture.CreateAsyncScope();
        var configured = fixtureScope.ServiceProvider.GetRequiredService<AppDbContext>();
        NpgsqlConnection? admin = null;
        try
        {
            if (configured.Database.IsSqlite())
                options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.FullName, "upgrade.db"), Pooling = false }.ToString());
            else
            {
                admin = new NpgsqlConnection(fixture.DisposablePostgresConnection);
                await admin.OpenAsync(Cancellation);
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
                await create.ExecuteNonQueryAsync(Cancellation);
                options.UseNpgsql(new NpgsqlConnectionStringBuilder(fixture.DisposablePostgresConnection) { SearchPath = schema }.ToString());
            }
            var owner = Guid.NewGuid();
            var person = Guid.NewGuid();
            await using (var previous = new AppDbContext(options.Options))
            {
                var migration = previous.Database.GetMigrations().Single(id => id.EndsWith("_AddLocalAccess", StringComparison.Ordinal));
                await previous.GetService<IMigrator>().MigrateAsync(migration, Cancellation);
                previous.UserProfiles.Add(new() { Id = owner, DisplayName = "Before upgrade" });

                await previous.SaveChangesAsync(Cancellation);
                object timestamp = previous.Database.IsSqlite() ? DateTimeOffset.UnixEpoch.UtcTicks : DateTimeOffset.UnixEpoch;
                await previous.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "People" ("Id", "UserProfileId", "DisplayName", "Notes", "RelationshipContext", "CreatedAt", "UpdatedAt")
                    VALUES ({person}, {owner}, {"Retained person"}, {"Retained"}, {"Unknown"}, {timestamp}, {timestamp})
                    """, Cancellation);
            }
            await using var upgraded = new AppDbContext(options.Options);
            await upgraded.Database.MigrateAsync(Cancellation);
            Assert.False(upgraded.Database.HasPendingModelChanges());
            Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync(Cancellation));
            Assert.Equal("Before upgrade", (await upgraded.UserProfiles.SingleAsync(profile => profile.Id == owner, Cancellation)).DisplayName);
            Assert.True(await upgraded.People.AnyAsync(row => row.Id == person && row.UserProfileId == owner, Cancellation));
            Assert.Empty(await upgraded.Set<ProfileDeletionCleanupMarker>().ToArrayAsync(Cancellation));
        }
        finally
        {
            if (admin is not null)
            {
                await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
                await admin.DisposeAsync();
            }
            directory.Delete(true); // Exact generated disposable directory, never an application-data root.
        }
    }

    [Fact]
    public async Task HTTP_delete_is_secure_and_confirmed_and_empty_selection_survives_new_host()
    {
        var owner = await fixture.CreateUserProfileAsync();
        using var host = Host();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1");
        await using var scope = host.Services.CreateAsyncScope();
        var version = (await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation)).Version;
        client.DefaultRequestHeaders.Remove("X-SocialTelemetry-ProfileId");
        client.DefaultRequestHeaders.Remove("X-SocialTelemetry-ProfileVersion");
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-ProfileId", owner.ToString());
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-ProfileVersion", version.ToString());
        using (var denied = await DeleteHttpAsync(false)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var csrf = await client.GetAsync("/local-access/antiforgery", Cancellation))
        {
            csrf.EnsureSuccessStatusCode();
            var json = await csrf.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
            client.DefaultRequestHeaders.Add("X-SocialTelemetry-CSRF", json.GetProperty("requestToken").GetString());
        }
        using (var denied = await DeleteHttpAsync(false)) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using (var renamed = await client.PatchAsJsonAsync($"/local-profiles/{owner}/name", new { DisplayName = "Renamed", ExpectedVersion = version }, Cancellation))
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using (var deleted = await DeleteHttpAsync(true))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            var json = await deleted.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("userProfileId").ValueKind);
        }
        using var restarted = Host();
        await using var checking = restarted.Services.CreateAsyncScope();
        var selection = await checking.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        Assert.Null(selection.UserProfileId); Assert.False(selection.LegacySingleProfileAccess);
        Assert.False(await checking.ServiceProvider.GetRequiredService<AppDbContext>().UserProfiles.AnyAsync(profile => profile.Id == owner, Cancellation));

        Task<HttpResponseMessage> DeleteHttpAsync(bool confirmed) => client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/local-profiles/{owner}")
            { Content = JsonContent.Create(new { ExpectedVersion = version, ConfirmJournalDeletion = confirmed }) }, Cancellation);
    }

    private static async Task<Guid> ResolveAsync(IServiceProvider services, Guid owner)
    {
        var selected = await services.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        await services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
            .ExecuteAsync(owner, selected.Version, Cancellation);
        return selected.Version;
    }

    private static async Task<AccessChange> SignInAsync(IServiceProvider services, Guid owner, string login)
    {
        await using var scope = services.CreateAsyncScope();
        var selected = await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        return await scope.ServiceProvider.GetRequiredService<ILocalAccessManagement>().SignInAsync(new(login, Password, owner, selected.Version, true), Cancellation);
    }

    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await continuation(); });
            next(application);
        };
    }

    private sealed class DeletionFailure(bool commit) : DbTransactionInterceptor, ISaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && commit) { Armed = false; throw new InvalidOperationException("Synthetic deletion commit failure"); }
            return ValueTask.FromResult(result);
        }
        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !commit && eventData.Context!.ChangeTracker.Entries<ProfileDeletionCleanupMarker>().Any())
            { Armed = false; throw new InvalidOperationException("Synthetic deletion save failure"); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AdmissionReadGate : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FROM \"LocalProfileSelection\"", StringComparison.Ordinal))
            {
                Armed = false;
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailedDeleteStorage(IAttachmentStorage backing) : IAttachmentStorage
    {
        public bool Armed { get; set; }
        public Task<string> SaveAsync(Stream content, string fileName, string? mimeType, CancellationToken cancellationToken) => backing.SaveAsync(content, fileName, mimeType, cancellationToken);
        public Task<bool> CompleteUploadAsync(string key, CancellationToken cancellationToken) => backing.CompleteUploadAsync(key, cancellationToken);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => backing.ExistsAsync(key, cancellationToken);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken) => backing.OpenReadAsync(key, cancellationToken);
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Armed ? throw new IOException("Synthetic cleanup failure") : backing.DeleteAsync(key, cancellationToken);
        public Task DeleteStagedAsync(string key, CancellationToken cancellationToken) => backing.DeleteStagedAsync(key, cancellationToken);
        public IEnumerable<StoredAttachmentFile> EnumerateFiles() => backing.EnumerateFiles();
        public IEnumerable<StoredAttachmentFile> EnumerateStagedFiles() => backing.EnumerateStagedFiles();
    }
}

public sealed class OpenedProfileLifecycleTests(PeopleApiFixture fixture) : OpenedProfileLifecycleTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteOpenedProfileLifecycleTests(SqliteApiFixture fixture) : OpenedProfileLifecycleTestsContract<SqliteApiFixture>(fixture);
