using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Tests.Features.People;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using SocialTelemetry.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class LocalAccessLifecycleTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture> where TFixture : PeopleApiFixture
{
    private const string Password = "Synthetic-Lifecycle9!";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("sign-in")]
    [InlineData("password-change")]
    [InlineData("recovery")]
    public async Task Retained_scope_observes_other_hosts_durable_password_and_recovery_lockout(string action)
    {
        var owner = await fixture.CreateUserProfileAsync();
        var clock = new LocalAccessClock();
        using var application = fixture.WithServices(services => services.AddSingleton<TimeProvider>(clock));
        using var client = application.CreateClient();
        using var otherHost = fixture.WithServices(services => services.AddSingleton<TimeProvider>(clock));
        using var otherClient = otherHost.CreateClient();
        var login = "retained-" + Guid.NewGuid().ToString("N");
        AccessChange enrolled;
        await using (var enrolling = application.Services.CreateAsyncScope())
        {
            await ResolveAsync(enrolling.ServiceProvider, owner);
            enrolled = await enrolling.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        }
        AccessChange? signed = action == "password-change"
            ? await SignInAsync(application.Services, owner, login, true) : null;
        async Task<AccessChange> AttemptAsync(IServiceProvider services, bool correct)
        {
            var management = services.GetRequiredService<ILocalAccessManagement>();
            if (action == "recovery")
                return await management.RecoverAsync(new(login, correct ? enrolled.RecoveryCode! : new string('A', 64), "Replacement-Retained9!"), Cancellation);
            if (action == "password-change")
            {
                services.GetRequiredService<LocalAccessRequest>().SetEvidence(signed!.DeviceEvidence!);
                if (services.GetRequiredService<LocalProfileOperation>().Binding is null)
                    await ResolveAsync(services, owner);
                return await management.ChangePasswordAsync(new(correct ? Password : "Wrong-Synthetic9!", "Replacement-Retained9!"), Cancellation);
            }
            var selection = await services.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
            return await management.SignInAsync(new(login, correct ? Password : "Wrong-Synthetic9!", owner, selection.Version, true), Cancellation);
        }
        await using var retained = application.Services.CreateAsyncScope();
        var first = await Assert.ThrowsAsync<LocalAccessException>(() => AttemptAsync(retained.ServiceProvider, false));
        Assert.Equal(LocalAccessFailure.InvalidCredentials, first.Failure);
        var tracked = Assert.Single(retained.ServiceProvider.GetRequiredService<AppDbContext>().Users.Local);
        Assert.Null(action == "recovery" ? tracked.RecoveryLockoutEnd : tracked.LockoutEnd);
        for (var attempt = 2; attempt <= 5; attempt++)
        {
            await using var external = otherHost.Services.CreateAsyncScope();
            var failure = await Assert.ThrowsAsync<LocalAccessException>(() => AttemptAsync(external.ServiceProvider, false));
            Assert.Equal(attempt == 5 ? LocalAccessFailure.LockedOut : LocalAccessFailure.InvalidCredentials, failure.Failure);
        }
        await using var checking = application.Services.CreateAsyncScope();
        var database = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await database.Users.AsNoTracking().SingleAsync(account => account.UserProfileId == owner, Cancellation);
        var grantCount = await database.Set<LocalProfileGrant>().CountAsync(Cancellation);
        foreach (var correct in new[] { true, false })
        {
            var failure = await Assert.ThrowsAsync<LocalAccessException>(() => AttemptAsync(retained.ServiceProvider, correct));
            Assert.Equal(LocalAccessFailure.LockedOut, failure.Failure);
        }
        var after = await database.Users.AsNoTracking().SingleAsync(account => account.Id == before.Id, Cancellation);
        Assert.Equal(before.AccessFailedCount, after.AccessFailedCount);
        Assert.Equal(before.LockoutEnd, after.LockoutEnd);
        Assert.Equal(before.RecoveryFailures, after.RecoveryFailures);
        Assert.Equal(before.RecoveryLockoutEnd, after.RecoveryLockoutEnd);
        Assert.Equal(before.AccessEpoch, after.AccessEpoch);
        Assert.True(before.PasswordHash == after.PasswordHash);
        Assert.True(before.RecoveryHash == after.RecoveryHash);
        Assert.Equal(grantCount, await database.Set<LocalProfileGrant>().CountAsync(Cancellation));
        clock.Advance(TimeSpan.FromMinutes(5));
        var resumed = await AttemptAsync(retained.ServiceProvider, true);
        if (action == "sign-in") Assert.NotNull(resumed.DeviceEvidence);
        else if (action == "recovery") Assert.NotNull(resumed.RecoveryCode);
        else Assert.Equal(owner, resumed.Selection.UserProfileId);
    }

    [Fact]
    public async Task Failed_recovery_keeps_unrelated_pending_journal_changes_in_the_retained_context()
    {
        var owner = await fixture.CreateUserProfileAsync();
        using var application = fixture.WithServices(_ => { });
        using var client = application.CreateClient();
        var login = "pending-" + Guid.NewGuid().ToString("N");
        AccessChange enrolled;
        await using (var enrolling = application.Services.CreateAsyncScope())
        {
            await ResolveAsync(enrolling.ServiceProvider, owner);
            enrolled = await enrolling.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        }
        await using var retained = application.Services.CreateAsyncScope();
        var database = retained.ServiceProvider.GetRequiredService<AppDbContext>();
        var person = new SocialTelemetry.Domain.People.Person { UserProfileId = owner, DisplayName = "Unsaved synthetic person" };
        database.People.Add(person);
        var failure = await Assert.ThrowsAsync<LocalAccessException>(() => retained.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
            .RecoverAsync(new(login, enrolled.RecoveryCode!, "short"), Cancellation));
        Assert.Equal(LocalAccessFailure.InvalidInput, failure.Failure);
        Assert.Equal(EntityState.Added, database.Entry(person).State);
        Assert.Same(person, Assert.Single(database.People.Local));
        await using var checking = application.Services.CreateAsyncScope();
        Assert.False(await checking.ServiceProvider.GetRequiredService<AppDbContext>().People.AnyAsync(row => row.Id == person.Id, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Polling_never_extends_deadline_and_expiry_denies_direct_reused_scope(bool remember)
    {
        var owner = await fixture.CreateUserProfileAsync();
        var clock = new LocalAccessClock();
        using var application = fixture.WithServices(services => services.AddSingleton<TimeProvider>(clock));
        using var client = application.CreateClient();
        var login = await EnrollAsync(application.Services, owner);
        var signed = await SignInAsync(application.Services, owner, login, remember);
        await using var active = application.Services.CreateAsyncScope();
        active.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        await ResolveAsync(active.ServiceProvider, owner);
        var operation = active.ServiceProvider.GetRequiredService<LocalProfileOperation>();
        await clock.FirstTimer.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        var deadline = remember ? TimeSpan.FromDays(14) : TimeSpan.FromMinutes(30);
        clock.Advance(deadline - TimeSpan.FromSeconds(1));
        var status = await active.ServiceProvider.GetRequiredService<ILocalAccessManagement>().StatusAsync(Cancellation);
        Assert.False(status.Locked);
        var expires = status.ExpiresAt;
        for (var index = 0; index < 3; index++)
            Assert.Equal(expires, (await active.ServiceProvider.GetRequiredService<ILocalAccessManagement>().StatusAsync(Cancellation)).ExpiresAt);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AssertDeniedAsync(() => operation.EnterAsync(Cancellation));
        var current = await active.ServiceProvider.GetRequiredService<ILocalAccessManagement>().StatusAsync(Cancellation);
        Assert.True(current.Locked);
    }

    [Fact]
    public async Task Admitted_activity_extends_idle_but_never_eight_hour_absolute_expiry()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var clock = new LocalAccessClock();
        using var application = fixture.WithServices(services => services.AddSingleton<TimeProvider>(clock));
        using var client = application.CreateClient();
        var login = await EnrollAsync(application.Services, owner);
        var signed = await SignInAsync(application.Services, owner, login, false);
        await using var active = application.Services.CreateAsyncScope();
        active.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        await ResolveAsync(active.ServiceProvider, owner);
        var originalExpiry = (await active.ServiceProvider.GetRequiredService<ILocalAccessManagement>().StatusAsync(Cancellation)).ExpiresAt;
        for (var index = 0; index < 23; index++)
        {
            clock.Advance(TimeSpan.FromMinutes(20));
            await active.ServiceProvider.GetRequiredService<ILocalAccessManagement>().ActivityAsync(Cancellation);
        }
        Assert.Equal(originalExpiry, (await active.ServiceProvider.GetRequiredService<ILocalAccessManagement>().StatusAsync(Cancellation)).ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(20));
        await AssertDeniedAsync(() => active.ServiceProvider.GetRequiredService<LocalProfileOperation>().EnterAsync(Cancellation));
    }

    [Fact]
    public async Task Remembered_A_B_A_preserves_each_grant_but_explicit_lock_cannot_be_overridden()
    {
        var first = await fixture.CreateUserProfileAsync();
        var second = await fixture.CreateUserProfileAsync(select: false);
        var clock = new LocalAccessClock();
        using var application = fixture.WithServices(services => services.AddSingleton<TimeProvider>(clock));
        using var client = application.CreateClient();
        var loginA = await EnrollAsync(application.Services, first);
        await using (var selecting = application.Services.CreateAsyncScope())
        {
            var selector = selecting.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
            await selector.SelectAsync(second, (await selector.GetAsync(Cancellation)).Version, Cancellation);
        }
        var loginB = await EnrollAsync(application.Services, second);
        var signedA = await SignInAsync(application.Services, first, loginA, true);
        await SignInAsync(application.Services, second, loginB, true, signedA.DeviceEvidence);
        await using var returning = application.Services.CreateAsyncScope();
        returning.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signedA.DeviceEvidence!);
        var management = returning.ServiceProvider.GetRequiredService<ILocalAccessManagement>();
        var selectorA = returning.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
        await management.ResumeAsync(first, (await selectorA.GetAsync(Cancellation)).Version, Cancellation);
        Assert.Equal(first, (await management.StatusAsync(Cancellation)).UserProfileId);
        await management.LockAsync(first, Cancellation);
        await management.ResumeAsync(second, (await selectorA.GetAsync(Cancellation)).Version, Cancellation);
        var selected = await selectorA.GetAsync(Cancellation);
        await Assert.ThrowsAsync<LocalAccessException>(() => management.ResumeAsync(first, selected.Version, Cancellation));
    }

    [Fact]
    public async Task Recovery_throttle_is_durable_across_scopes_and_does_not_block_password_signin()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var clock = new LocalAccessClock();
        using var application = fixture.WithServices(services => services.AddSingleton<TimeProvider>(clock));
        using var client = application.CreateClient();
        var login = await EnrollAsync(application.Services, owner);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await using var failure = application.Services.CreateAsyncScope();
            var error = await Assert.ThrowsAsync<LocalAccessException>(() => failure.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .RecoverAsync(new(login, new string('A', 64), Password), Cancellation));
            Assert.Equal(attempt == 5 ? LocalAccessFailure.LockedOut : LocalAccessFailure.InvalidCredentials, error.Failure);
        }
        var signed = await SignInAsync(application.Services, owner, login, false);
        Assert.NotNull(signed.DeviceEvidence);
        await using var stored = application.Services.CreateAsyncScope();
        var account = await stored.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(row => row.UserProfileId == owner, Cancellation);
        Assert.True(account.RecoveryLockoutEnd > clock.GetUtcNow());
    }

    [Fact]
    public async Task Password_failures_are_durable_and_unknown_login_attempts_cannot_reset_host_limit()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var clock = new LocalAccessClock();
        using var application = fixture.WithServices(services => services.AddSingleton<TimeProvider>(clock));
        using var client = application.CreateClient();
        var login = await EnrollAsync(application.Services, owner);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await using var failed = application.Services.CreateAsyncScope();
            var selector = failed.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
            var selected = await selector.GetAsync(Cancellation);
            var error = await Assert.ThrowsAsync<LocalAccessException>(() => failed.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .SignInAsync(new(login, "Wrong-Synthetic9!", owner, selected.Version, false), Cancellation));
            Assert.Equal(attempt == 5 ? LocalAccessFailure.LockedOut : LocalAccessFailure.InvalidCredentials, error.Failure);
        }
        await using (var locked = application.Services.CreateAsyncScope())
        {
            var selected = await locked.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
            var error = await Assert.ThrowsAsync<LocalAccessException>(() => locked.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .SignInAsync(new(login, Password, owner, selected.Version, false), Cancellation));
            Assert.Equal(LocalAccessFailure.LockedOut, error.Failure);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull((await SignInAsync(application.Services, owner, login, false)).DeviceEvidence);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using var unknown = application.Services.CreateAsyncScope();
            var selected = await unknown.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
            var error = await Assert.ThrowsAsync<LocalAccessException>(() => unknown.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .SignInAsync(new("unknown-" + attempt, Password, owner, selected.Version, false), Cancellation));
            Assert.Equal(attempt == 9 ? LocalAccessFailure.LockedOut : LocalAccessFailure.InvalidCredentials, error.Failure);
        }
    }

    [Fact]
    public async Task Leaving_nonremembered_profile_closes_its_grant_instead_of_retaining_quick_entry()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        using var application = fixture.WithServices(_ => { });
        using var client = application.CreateClient();
        var login = await EnrollAsync(application.Services, owner);
        var signed = await SignInAsync(application.Services, owner, login, false);
        await using var selection = application.Services.CreateAsyncScope();
        selection.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        var selector = selection.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
        await selector.SelectAsync(other, signed.Selection.Version, Cancellation);
        var current = await selector.GetAsync(Cancellation);
        await Assert.ThrowsAsync<LocalAccessException>(() => selector.SelectAsync(owner, current.Version, Cancellation));
    }

    [Fact]
    public async Task Corrupt_framework_ticket_is_unavailable_and_key_location_failure_does_not_disable_password_protection()
    {
        var owner = await fixture.CreateUserProfileAsync();
        using var application = fixture.WithServices(_ => { });
        using var client = application.CreateClient();
        var login = await EnrollAsync(application.Services, owner);
        var signed = await SignInAsync(application.Services, owner, login, true);
        var store = application.Services.GetRequiredService<LocalDeviceTicketStore>();
        await using var scope = application.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = LocalAccessValidation.TokenHash(signed.DeviceEvidence!.DeviceToken!);
        await database.Set<LocalDeviceTicket>().Where(ticket => ticket.TokenHash == hash)
            .ExecuteUpdateAsync(update => update.SetProperty(ticket => ticket.ProtectedTicket, new byte[] { 1, 2, 3 }), Cancellation);
        Assert.Null(await store.RetrieveAsync(signed.DeviceEvidence.DeviceToken!));
        var directory = application.Services.GetRequiredService<IOptions<LocalAccessOptions>>().Value.DataDirectory;
        var keys = application.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = keys.CreateProtector("Synthetic local-access test.v1");
        var protectedBytes = protector.Protect(new byte[] { 8, 9, 10 });
        Assert.Equal(new byte[] { 8, 9, 10 }, protector.Unprotect(protectedBytes));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => keys.CreateProtector("Other purpose").Unprotect(protectedBytes));
        var invalidLocation = Path.Combine(directory, "test-file-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(invalidLocation, "Synthetic location conflict", Cancellation);
        try
        {
            var ring = new LocalAccessKeyRing(application.Services.GetRequiredService<IServiceScopeFactory>(),
                application.Services.GetRequiredService<ApplicationPaths>(), Options.Create(new LocalAccessOptions { DataDirectory = invalidLocation }));
            var denied = await Assert.ThrowsAsync<LocalAccessException>(() => ring.StartAsync(Cancellation));
            Assert.Equal(LocalAccessFailure.StorageUnavailable, denied.Failure);
        }
        finally { File.Delete(invalidLocation); }
        Assert.Equal(LocalProtectionStatus.Enabled, await database.Users.Where(account => account.UserProfileId == owner)
            .Select(account => account.LocalProtection).SingleAsync(Cancellation));
        if (!OperatingSystem.IsWindows())
        {
            var keyFiles = Directory.GetFiles(directory, "key-*.xml", SearchOption.AllDirectories);
            Assert.NotEmpty(keyFiles);
            foreach (var file in keyFiles)
                Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
            var original = File.GetUnixFileMode(directory);
            try
            {
                File.SetUnixFileMode(directory, original | UnixFileMode.GroupRead);
                var ring = new LocalAccessKeyRing(application.Services.GetRequiredService<IServiceScopeFactory>(),
                    application.Services.GetRequiredService<ApplicationPaths>(), Options.Create(new LocalAccessOptions { DataDirectory = directory }));
                var denied = await Assert.ThrowsAsync<LocalAccessException>(() => ring.StartAsync(Cancellation));
                Assert.Equal(LocalAccessFailure.StorageUnavailable, denied.Failure);
            }
            finally { File.SetUnixFileMode(directory, original); }
        }
    }

    [Fact]
    public async Task Failed_pending_upload_does_not_extend_activity_but_successful_ready_commit_does()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = owner, Title = "Synthetic activity", OccurredAt = DateTimeOffset.UtcNow };
        await using (var seed = fixture.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            database.Interactions.Add(interaction);
            await database.SaveChangesAsync(Cancellation);
        }
        var clock = new LocalAccessClock();
        var fault = new FailReadyUpload();
        using var application = fixture.WithServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(fault));
        });
        using var client = application.CreateClient();
        var login = await EnrollAsync(application.Services, owner);
        var signed = await SignInAsync(application.Services, owner, login, false);
        DateTimeOffset signedAt;
        await using (var before = application.Services.CreateAsyncScope())
        {
            var database = before.ServiceProvider.GetRequiredService<AppDbContext>();
            signedAt = await database.Set<LocalProfileGrant>().Where(grant => !grant.Revoked && database.Users.Any(account =>
                account.Id == grant.AccountId && account.UserProfileId == owner)).Select(grant => grant.LastActivityAt).SingleAsync(Cancellation);
        }
        clock.Advance(TimeSpan.FromMinutes(20));
        await using (var uploading = application.Services.CreateAsyncScope())
        {
            uploading.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
            await ResolveAsync(uploading.ServiceProvider, owner);
            fault.Target = uploading.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = new SocialTelemetry.Application.Features.Interactions.AddAttachment.UploadFile(3, "synthetic.wav", "audio/wav",
                () => new MemoryStream(new byte[] { 1, 2, 3 }));
            await Assert.ThrowsAsync<SyntheticReadyFailure>(() => uploading.ServiceProvider
                .GetRequiredService<SocialTelemetry.Application.Features.Interactions.AddAttachment.Scenario>()
                .ExecuteAsync(new(interaction.Id, AttachmentType.Audio, null, file), Cancellation));
        }
        await using (var check = application.Services.CreateAsyncScope())
        {
            var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(signedAt, await database.Set<LocalProfileGrant>().Where(grant => !grant.Revoked && database.Users.Any(account =>
                account.Id == grant.AccountId && account.UserProfileId == owner))
                .Select(grant => grant.LastActivityAt).SingleAsync(Cancellation));
        }
        await using var successful = application.Services.CreateAsyncScope();
        successful.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        await ResolveAsync(successful.ServiceProvider, owner);
        await successful.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.Interactions.AddAttachment.Scenario>()
            .ExecuteAsync(new(interaction.Id, AttachmentType.Text, "Synthetic ready text", null), Cancellation);
        var stored = successful.ServiceProvider.GetRequiredService<AppDbContext>();
        var lastActivity = await stored.Set<LocalProfileGrant>().Where(grant => !grant.Revoked && stored.Users.Any(account =>
            account.Id == grant.AccountId && account.UserProfileId == owner)).Select(grant => grant.LastActivityAt).SingleAsync(Cancellation);
        // PostgreSQL timestamps have microsecond precision.
        Assert.InRange(lastActivity, clock.GetUtcNow().AddTicks(-10), clock.GetUtcNow());
    }

    private sealed class SyntheticReadyFailure : Exception;
    private sealed class FailReadyUpload : SaveChangesInterceptor
    {
        public AppDbContext? Target { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Target is not null && ReferenceEquals(eventData.Context, Target) && Target.ChangeTracker.Entries<InteractionAttachment>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.Status == AttachmentStatus.Ready))
                throw new SyntheticReadyFailure();
            return ValueTask.FromResult(result);
        }
    }

    private static async Task AssertDeniedAsync(Func<Task<LocalOperationAdmission>> attempt)
    {
        var exception = await Record.ExceptionAsync(async () => { using var admission = await attempt(); });
        Assert.True(exception is LocalAccessException or OperationCanceledException or LocalProfileAccessException,
            $"Expected revoked/expired admission, got {exception?.GetType().Name ?? "success"}.");
    }

    private static async Task<string> EnrollAsync(IServiceProvider services, Guid owner)
    {
        await using var scope = services.CreateAsyncScope();
        await ResolveAsync(scope.ServiceProvider, owner);
        var login = "lifecycle-" + Guid.NewGuid().ToString("N");
        await scope.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        return login;
    }

    private static async Task<AccessChange> SignInAsync(IServiceProvider services, Guid owner, string login, bool remember, LocalAccessEvidence? evidence = null)
    {
        await using var scope = services.CreateAsyncScope();
        if (evidence is not null) scope.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(evidence);
        var selection = await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        return await scope.ServiceProvider.GetRequiredService<ILocalAccessManagement>().SignInAsync(new(login, Password, owner, selection.Version, remember), Cancellation);
    }

    private static async Task ResolveAsync(IServiceProvider services, Guid owner)
    {
        var selection = await services.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        await services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>().ExecuteAsync(owner, selection.Version, Cancellation);
    }
}

public sealed class LocalAccessLifecycleTests(PeopleApiFixture fixture) : LocalAccessLifecycleTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteLocalAccessLifecycleTests(SqliteApiFixture fixture) : LocalAccessLifecycleTestsContract<SqliteApiFixture>(fixture);
