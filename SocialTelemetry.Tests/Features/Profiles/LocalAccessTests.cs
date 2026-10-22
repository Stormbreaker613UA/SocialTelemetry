using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class LocalAccessTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>, IDisposable where TFixture : PeopleApiFixture
{
    // Each test is a separate host/device attempt window. Do not relax the real host throttle
    // merely because xUnit shares the database fixture between independent tests.
    private readonly WebApplicationFactory<Program> application = fixture.WithServices(_ => { });
    private AsyncServiceScope CreateAsyncScope() => application.Services.CreateAsyncScope();
    public void Dispose() => application.Dispose();
    private const string Password = "Synthetic-LocalPassword9!";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enable_requires_real_admission_and_enforces_protection_on_direct_reads()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var login = "local-" + Guid.NewGuid().ToString("N");
        await using var scope = CreateAsyncScope();
        var services = scope.ServiceProvider;
        var management = services.GetRequiredService<ILocalAccessManagement>();
        var selector = services.GetRequiredService<LocalProfileSelectionService>();
        var before = await selector.GetAsync(Cancellation);
        services.GetRequiredService<LocalProfileOperation>().Bind(new(owner, before.Version), CancellationToken.None);
        var fabricated = await Assert.ThrowsAsync<LocalAccessException>(() => management.EnableAsync(new(login, Password), Cancellation));
        Assert.Equal(LocalAccessFailure.Required, fabricated.Failure);
        Assert.False(await services.GetRequiredService<AppDbContext>().Users.AnyAsync(account => account.UserProfileId == owner, Cancellation));

        // A fresh scope obtains the existing real unprotected resolver, not a test bypass.
        await using var enrollment = CreateAsyncScope();
        await ResolveAsync(enrollment.ServiceProvider, owner);
        var enabled = await enrollment.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        Assert.Equal(64, enabled.RecoveryCode?.Length);
        Assert.NotEqual(before.Version, enabled.Selection.Version);
        await using var denied = CreateAsyncScope();
        var error = await Assert.ThrowsAsync<LocalAccessException>(() => ResolveAsync(denied.ServiceProvider, owner));
        Assert.Equal(LocalAccessFailure.Required, error.Failure);
        var stored = await denied.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(user => user.UserProfileId == owner, Cancellation);
        Assert.Equal(LocalProtectionStatus.Enabled, stored.LocalProtection);
        Assert.NotEqual(Password, stored.PasswordHash);
        Assert.DoesNotContain(enabled.RecoveryCode!, stored.RecoveryHash!);
    }

    [Fact]
    public async Task Remembered_device_grant_is_profile_specific_and_lock_cannot_be_replayed()
    {
        var first = await EnrollAsync();
        var second = await EnrollAsync();
        var signed = await SignInAsync(first.Owner, first.Login, Password, true);
        await using var original = CreateAsyncScope();
        original.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        await ResolveAsync(original.ServiceProvider, first.Owner);
        var operation = original.ServiceProvider.GetRequiredService<LocalProfileOperation>();
        using (await operation.EnterAsync(Cancellation)) { }

        // A's device cookie is not authorization for B.
        await using var foreign = CreateAsyncScope();
        foreign.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        var selector = foreign.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
        await Assert.ThrowsAsync<LocalAccessException>(() => selector.SelectAsync(second.Owner, signed.Selection.Version, Cancellation));

        await using var locking = CreateAsyncScope();
        locking.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        await locking.ServiceProvider.GetRequiredService<ILocalAccessManagement>().LockAsync(first.Owner, Cancellation);
        Assert.True(operation.Lifetime.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var ignored = await operation.EnterAsync(Cancellation); });
        await using var replay = CreateAsyncScope();
        replay.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        var current = await replay.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        await Assert.ThrowsAsync<LocalAccessException>(() => replay.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
            .ResumeAsync(first.Owner, current.Version, Cancellation));
    }

    [Fact]
    public async Task Recovery_is_single_use_and_invalid_new_password_does_not_consume_code()
    {
        var profile = await EnrollAsync();
        await using (var invalid = CreateAsyncScope())
        {
            var error = await Assert.ThrowsAsync<LocalAccessException>(() => invalid.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .RecoverAsync(new(profile.Login, profile.Code, "short"), Cancellation));
            Assert.Equal(LocalAccessFailure.InvalidInput, error.Failure);
        }
        AccessChange recovered;
        await using (var valid = CreateAsyncScope())
            recovered = await valid.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .RecoverAsync(new(profile.Login, profile.Code, "Replacement-Password9!"), Cancellation);
        Assert.NotEqual(profile.Code, recovered.RecoveryCode);
        await using var replay = CreateAsyncScope();
        var failed = await Assert.ThrowsAsync<LocalAccessException>(() => replay.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
            .RecoverAsync(new(profile.Login, profile.Code, "Another-Password9!"), Cancellation));
        Assert.Equal(LocalAccessFailure.InvalidCredentials, failed.Failure);
        var signed = await SignInAsync(profile.Owner, profile.Login, "Replacement-Password9!", false);
        Assert.NotNull(signed.DeviceEvidence);
    }

    [Fact]
    public async Task Disable_preserves_account_id_and_journal_but_revokes_old_access()
    {
        var profile = await EnrollAsync();
        var signed = await SignInAsync(profile.Owner, profile.Login, Password, true);
        Guid accountId;
        await using (var scope = CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
            await ResolveAsync(scope.ServiceProvider, profile.Owner);
            accountId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(account => account.UserProfileId == profile.Owner)
                .Select(account => account.Id).SingleAsync(Cancellation);
            await scope.ServiceProvider.GetRequiredService<ILocalAccessManagement>().DisableAsync(Password, Cancellation);
        }
        await using var check = CreateAsyncScope();
        await ResolveAsync(check.ServiceProvider, profile.Owner);
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await database.Users.AsNoTracking().SingleAsync(user => user.UserProfileId == profile.Owner, Cancellation);
        Assert.Equal(accountId, account.Id);
        Assert.Equal(LocalProtectionStatus.Disabled, account.LocalProtection);
        Assert.Null(account.PasswordHash);
        Assert.Null(account.RecoveryHash);
        Assert.True(await database.UserProfiles.AnyAsync(row => row.Id == profile.Owner, Cancellation));
        Assert.False(await database.Set<LocalProfileGrant>().AnyAsync(grant => grant.AccountId == accountId && !grant.Revoked, Cancellation));
    }

    [Fact]
    public async Task Concurrent_recovery_has_one_winner_and_SQL_commit_failure_preserves_password_code_and_grants()
    {
        var profile = await EnrollAsync();
        var fault = new RecoveryCommitFailure();
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(fault)));
        using var client = application.CreateClient();
        AccessChange signed;
        await using (var signing = application.Services.CreateAsyncScope())
        {
            var selector = signing.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
            signed = await signing.ServiceProvider.GetRequiredService<ILocalAccessManagement>().SignInAsync(
                new(profile.Login, Password, profile.Owner, (await selector.GetAsync(Cancellation)).Version, true), Cancellation);
        }
        await using (var failed = application.Services.CreateAsyncScope())
        {
            fault.Target = failed.ServiceProvider.GetRequiredService<AppDbContext>();
            await Assert.ThrowsAsync<SyntheticRecoveryFailure>(() => failed.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .RecoverAsync(new(profile.Login, profile.Code, "Replacement-Concurrent9!"), Cancellation));
            fault.Target = null;
            var invalid = await Assert.ThrowsAsync<LocalAccessException>(() => failed.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .RecoverAsync(new(profile.Login, profile.Code, "short"), Cancellation));
            Assert.Equal(LocalAccessFailure.InvalidInput, invalid.Failure);
        }
        await using (var retained = application.Services.CreateAsyncScope())
        {
            retained.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
            await ResolveAsync(retained.ServiceProvider, profile.Owner);
            using var admission = await retained.ServiceProvider.GetRequiredService<LocalProfileOperation>().EnterAsync(Cancellation);
            var database = retained.ServiceProvider.GetRequiredService<AppDbContext>();
            var account = await database.Users.SingleAsync(row => row.UserProfileId == profile.Owner, Cancellation);
            Assert.True(await retained.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>().CheckPasswordAsync(account, Password));
        }
        using var cooperating = fixture.WithServices(_ => { });
        using var cooperatingClient = cooperating.CreateClient();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> AttemptAsync(IServiceProvider services)
        {
            await using var scope = services.CreateAsyncScope();
            await start.Task.WaitAsync(Cancellation);
            try
            {
                var changed = await scope.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                    .RecoverAsync(new(profile.Login, profile.Code, "Replacement-Concurrent9!"), Cancellation);
                Assert.NotEqual(profile.Code, changed.RecoveryCode);
                return true;
            }
            catch (LocalAccessException exception) when (exception.Failure == LocalAccessFailure.InvalidCredentials) { return false; }
        }
        fault.Target = null;
        var first = AttemptAsync(application.Services);
        var second = AttemptAsync(cooperating.Services);
        start.TrySetResult();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Single(outcomes, succeeded => succeeded);
        await using var revoked = application.Services.CreateAsyncScope();
        revoked.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        await Assert.ThrowsAsync<LocalAccessException>(() => ResolveAsync(revoked.ServiceProvider, profile.Owner));
    }

    private sealed class SyntheticRecoveryFailure : Exception;
    private sealed class RecoveryCommitFailure : DbTransactionInterceptor
    {
        public AppDbContext? Target { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(eventData.Context, Target)) throw new SyntheticRecoveryFailure();
            return ValueTask.FromResult(result);
        }
    }

    private async Task<(Guid Owner, string Login, string Code)> EnrollAsync()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var login = "local-" + Guid.NewGuid().ToString("N");
        await using var scope = CreateAsyncScope();
        await ResolveAsync(scope.ServiceProvider, owner);
        var result = await scope.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, Password), Cancellation);
        return (owner, login, result.RecoveryCode!);
    }

    private async Task<AccessChange> SignInAsync(Guid owner, string login, string password, bool remember)
    {
        await using var scope = CreateAsyncScope();
        var selection = await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        return await scope.ServiceProvider.GetRequiredService<ILocalAccessManagement>().SignInAsync(new(login, password, owner, selection.Version, remember), Cancellation);
    }

    private static async Task ResolveAsync(IServiceProvider services, Guid owner)
    {
        var selected = await services.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        await services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
            .ExecuteAsync(owner, selected.Version, Cancellation);
    }
}

public sealed class LocalAccessTests(PeopleApiFixture fixture) : LocalAccessTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteLocalAccessTests(SqliteApiFixture fixture) : LocalAccessTestsContract<SqliteApiFixture>(fixture);
