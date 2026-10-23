using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Analysis;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;
using SocialTelemetry.Tests.Features.People;
using AppAnalysis = SocialTelemetry.Application.Features.Analysis.AnalyzeInteraction;
using ProposedUpdate = SocialTelemetry.Application.Contracts.AI.Models.SuggestedProfileUpdate;

namespace SocialTelemetry.Tests.Features.Analysis;

public abstract class AnalysisApplicationTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Direct_analysis_requires_bound_owner_and_rejects_foreign_context_before_provider()
    {
        var data = await SeedAsync();
        var other = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<AppDbContext>();
        var provider = new ProbeProvider(database, new CommitObserver());
        var scenario = CreateScenario(services, provider);
        var unbound = await Assert.ThrowsAsync<LocalProfileAccessException>(() => scenario.ExecuteAsync(new(data.InteractionId, null, []), Cancellation));
        Assert.Equal(LocalProfileFailure.SelectionRequired, unbound.Failure);
        await BindAsync(services, other);
        await Assert.ThrowsAsync<NotFoundException>(() => scenario.ExecuteAsync(new(data.InteractionId, null, []), Cancellation));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, provider.Leases);
        Assert.Null(database.Database.CurrentTransaction);
        Assert.Equal(1, await database.InteractionAnalyses.CountAsync(analysis => analysis.InteractionId == data.InteractionId, Cancellation));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("lease")]
    [InlineData("commit")]
    public async Task Direct_analysis_keeps_snapshot_inference_save_lease_commit_order_and_atomic_rollback(string failure)
    {
        var data = await SeedAsync();
        var observer = new CommitObserver { FailCommit = failure == "commit" };
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(observer)));
        ProbeProvider provider;
        await using (var scope = application.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            await BindAsync(services, data.Owner);
            var database = services.GetRequiredService<AppDbContext>();
            observer.Target = database;
            provider = new ProbeProvider(database, observer) { FailLease = failure == "lease" };
            var scenario = CreateScenario(services, provider);
            if (failure == "lease")
            {
                var rejected = await Assert.ThrowsAsync<AiProviderException>(() => scenario.ExecuteAsync(new(data.InteractionId, null, []), Cancellation));
                Assert.Equal(AiFailure.ExecutionInvalidated, rejected.Failure);
            }
            else if (failure == "commit")
                await Assert.ThrowsAsync<SyntheticCommitFailure>(() => scenario.ExecuteAsync(new(data.InteractionId, null, []), Cancellation));
            else
            {
                var result = await scenario.ExecuteAsync(new(data.InteractionId, null, []), Cancellation);
                Assert.Equal("synthetic-provider", result.Provider);
                Assert.Equal(AppAnalysis.AnalysisContract.SchemaVersion, result.SchemaVersion);
                Assert.Equal(AppAnalysis.AnalysisContract.PromptVersion, result.PromptVersion);
            }
            Assert.True(observer.SnapshotCommitted);
            Assert.True(provider.ValidatedAfterSqlSave);
            Assert.Equal(1, provider.Leases);
            Assert.Equal(1, provider.DisposedLeases);
            Assert.Equal(failure == "none", provider.DisposedAfterCommit);
            Assert.Equal(failure == "none", observer.FinalCommitted);
            Assert.Null(database.Database.CurrentTransaction);
        }
        await using var verification = fixture.CreateAsyncScope();
        var stored = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(failure == "none" ? 2 : 1, await stored.InteractionAnalyses.CountAsync(analysis => analysis.InteractionId == data.InteractionId, Cancellation));
        Assert.Equal(failure == "none" ? 2 : 0, await stored.SuggestedProfileUpdates.CountAsync(suggestion => suggestion.InteractionAnalysis.InteractionId == data.InteractionId, Cancellation));
        var historical = await stored.InteractionAnalyses.SingleAsync(analysis => analysis.Id == data.HistoricalId, Cancellation);
        Assert.Equal("Historical summary", historical.Summary);
        Assert.Null(historical.ResultJson);
        Assert.Equal("Original notes", await stored.People.Where(person => person.Id == data.PersonId).Select(person => person.Notes).SingleAsync(Cancellation));
        Assert.False(await stored.PersonFacts.AnyAsync(fact => fact.PersonId == data.PersonId, Cancellation));
    }

    [Fact]
    public async Task Direct_source_change_during_inference_rejects_before_lease_and_preserves_old_analysis()
    {
        var data = await SeedAsync();
        var observer = new CommitObserver();
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(observer)));
        await using var scope = application.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await BindAsync(services, data.Owner);
        var database = services.GetRequiredService<AppDbContext>();
        observer.Target = database;
        var provider = new ProbeProvider(database, observer)
        {
            DuringInference = async () =>
            {
                await using var writer = fixture.CreateAsyncScope();
                var writing = writer.ServiceProvider.GetRequiredService<AppDbContext>();
                var interaction = await writing.Interactions.SingleAsync(interaction => interaction.Id == data.InteractionId, Cancellation);
                interaction.Description = "Changed during inference";
                await writing.SaveChangesAsync(Cancellation);
            }
        };
        var stale = await Assert.ThrowsAsync<AiProviderException>(() => CreateScenario(services, provider).ExecuteAsync(new(data.InteractionId, null, []), Cancellation));
        Assert.Equal(AiFailure.StaleContext, stale.Failure);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, provider.Leases);
        Assert.Null(database.Database.CurrentTransaction);
        Assert.Equal(1, await database.InteractionAnalyses.CountAsync(analysis => analysis.InteractionId == data.InteractionId, Cancellation));
        Assert.False(await database.SuggestedProfileUpdates.AnyAsync(suggestion => suggestion.InteractionAnalysis.InteractionId == data.InteractionId, Cancellation));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("model")]
    [InlineData("inference")]
    public async Task Direct_profile_switch_cancels_original_operation_even_when_awaited_work_ignores_cancellation(string phase)
    {
        var data = await SeedAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        var observer = new CommitObserver();
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(observer)));
        await using var scope = application.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await BindAsync(services, data.Owner);
        var database = services.GetRequiredService<AppDbContext>();
        observer.Target = database;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task PauseAsync()
        {
            entered.TrySetResult();
            // Deliberately ignore the operation token, like a provider/context reader might.
            await resume.Task.WaitAsync(deadline.Token);
        }
        var persistence = new BoundaryPersistence(services.GetRequiredService<IAnalysisPersistence>())
        {
            AfterSnapshot = phase == "snapshot" ? PauseAsync : null
        };
        var provider = new ProbeProvider(database, observer)
        {
            DuringModel = phase == "model" ? PauseAsync : null,
            DuringInference = phase == "inference" ? PauseAsync : null
        };
        var execution = CreateScenario(services, provider, persistence).ExecuteAsync(new(data.InteractionId, null, []), Cancellation);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            Assert.Null(database.Database.CurrentTransaction);
            await SwitchAsync(application.Services, other);
            Assert.False(Cancellation.IsCancellationRequested);
            Assert.True(services.GetRequiredService<LocalProfileOperation>().Lifetime.IsCancellationRequested);
            Assert.True(persistence.SnapshotToken.IsCancellationRequested);
            if (phase == "model") Assert.True(provider.ModelCancellation.IsCancellationRequested);
            if (phase == "inference") Assert.True(provider.InferenceCancellation.IsCancellationRequested);
            resume.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(phase == "inference" ? 1 : 0, provider.Calls);
        Assert.Equal(0, provider.Leases);
        Assert.Null(database.Database.CurrentTransaction);
        await AssertHistoricalOnlyAsync(data);
    }

    [Fact]
    public async Task Direct_commit_winner_returns_success_and_keeps_original_owner_after_later_profile_switch()
    {
        var data = await SeedAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        var observer = new CommitObserver();
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(observer)));
        await using var scope = application.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await BindAsync(services, data.Owner);
        var database = services.GetRequiredService<AppDbContext>();
        observer.Target = database;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new BoundaryPersistence(services.GetRequiredService<IAnalysisPersistence>())
        {
            AfterCommit = async () =>
            {
                committed.TrySetResult();
                await resume.Task.WaitAsync(deadline.Token);
            }
        };
        var provider = new ProbeProvider(database, observer);
        var execution = CreateScenario(services, provider, persistence).ExecuteAsync(new(data.InteractionId, null, []), Cancellation);
        try
        {
            await committed.Task.WaitAsync(deadline.Token);
            Assert.True(observer.FinalCommitted);
            Assert.Equal(1, provider.DisposedLeases);
            Assert.Null(database.Database.CurrentTransaction);
            await SwitchAsync(application.Services, other);
            Assert.True(services.GetRequiredService<LocalProfileOperation>().Lifetime.IsCancellationRequested);
            resume.TrySetResult();
            var result = await execution;
            await using var verification = fixture.CreateAsyncScope();
            var stored = verification.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(data.Owner, await stored.InteractionAnalyses.Where(analysis => analysis.Id == result.Id)
                .Select(analysis => analysis.Interaction.UserProfileId).SingleAsync(Cancellation));
            Assert.Equal(2, await stored.SuggestedProfileUpdates.CountAsync(suggestion => suggestion.InteractionAnalysisId == result.Id && suggestion.Status == SuggestionStatus.Pending, Cancellation));
            Assert.Equal(2, await stored.InteractionAnalyses.CountAsync(analysis => analysis.InteractionId == data.InteractionId, Cancellation));
            Assert.False(await stored.PersonFacts.AnyAsync(fact => fact.PersonId == data.PersonId, Cancellation));
        }
        finally { resume.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_caller_cancellation_and_execution_invalidation_keep_distinct_failure_mapping(bool invalidateExecution)
    {
        var data = await SeedAsync();
        var observer = new CommitObserver();
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(observer)));
        await using var scope = application.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await BindAsync(services, data.Owner);
        var database = services.GetRequiredService<AppDbContext>();
        observer.Target = database;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        using var executionLifetime = new CancellationTokenSource();
        var provider = new ProbeProvider(database, observer)
        {
            LifetimeToken = executionLifetime.Token,
            DuringInference = () =>
            {
                if (invalidateExecution) executionLifetime.Cancel();
                else caller.Cancel();
                return Task.CompletedTask;
            }
        };
        var execution = CreateScenario(services, provider).ExecuteAsync(new(data.InteractionId, null, []), caller.Token);
        if (invalidateExecution)
        {
            var rejected = await Assert.ThrowsAsync<AiProviderException>(() => execution);
            Assert.Equal(AiFailure.ExecutionInvalidated, rejected.Failure);
            Assert.False(provider.InferenceCancellation.IsCancellationRequested);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.True(provider.InferenceCancellation.IsCancellationRequested);
        }
        Assert.False(services.GetRequiredService<LocalProfileOperation>().Lifetime.IsCancellationRequested);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, provider.Leases);
        Assert.Null(database.Database.CurrentTransaction);
        await AssertHistoricalOnlyAsync(data);
    }

    [Theory]
    [InlineData("snapshot", "lock")]
    [InlineData("inference", "lock")]
    [InlineData("inference", "password")]
    [InlineData("inference", "recovery")]
    [InlineData("inference", "expiry")]
    [InlineData("commit", "lock")]
    public async Task Protected_direct_analysis_obeys_revocation_and_preserves_committed_winner(string phase, string revocation)
    {
        var data = await SeedAsync();
        var clock = new SocialTelemetry.Tests.Features.Profiles.LocalAccessClock();
        var observer = new CommitObserver();
        using var application = fixture.WithServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(observer));
        });
        using var client = application.CreateClient();
        const string password = "Synthetic-Protected9!";
        var login = "analysis-" + Guid.NewGuid().ToString("N");
        AccessChange enrolled;
        AccessChange signed;
        await using (var enrollment = application.Services.CreateAsyncScope())
        {
            await BindAsync(enrollment.ServiceProvider, data.Owner);
            var management = enrollment.ServiceProvider.GetRequiredService<ILocalAccessManagement>();
            enrolled = await management.EnableAsync(new(login, password), Cancellation);
        }
        await using (var signing = application.Services.CreateAsyncScope())
            signed = await signing.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .SignInAsync(new(login, password, data.Owner, enrolled.Selection.Version, false), Cancellation);
        await using var active = application.Services.CreateAsyncScope();
        var services = active.ServiceProvider;
        services.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
        await BindAsync(services, data.Owner);
        var operation = services.GetRequiredService<LocalProfileOperation>();
        var database = services.GetRequiredService<AppDbContext>();
        observer.Target = database;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task PauseAsync() { entered.TrySetResult(); await resume.Task.WaitAsync(deadline.Token); }
        var persistence = new BoundaryPersistence(services.GetRequiredService<IAnalysisPersistence>())
        {
            AfterSnapshot = phase == "snapshot" ? PauseAsync : null,
            AfterCommit = phase == "commit" ? PauseAsync : null
        };
        var provider = new ProbeProvider(database, observer) { DuringInference = phase == "inference" ? PauseAsync : null };
        var execution = CreateScenario(services, provider, persistence).ExecuteAsync(new(data.InteractionId, null, []), Cancellation);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            Assert.Null(database.Database.CurrentTransaction);
            if (revocation == "expiry")
            {
                await clock.FirstTimer.Task.WaitAsync(deadline.Token);
                var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = operation.Lifetime.Register(() => canceled.TrySetResult());
                clock.Advance(TimeSpan.FromMinutes(30));
                await canceled.Task.WaitAsync(deadline.Token);
            }
            else
            {
                await using var changing = application.Services.CreateAsyncScope();
                changing.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
                var management = changing.ServiceProvider.GetRequiredService<ILocalAccessManagement>();
                if (revocation == "recovery")
                    await management.RecoverAsync(new(login, enrolled.RecoveryCode!, "Synthetic-Replacement9!"), Cancellation);
                else if (revocation == "password")
                {
                    await BindAsync(changing.ServiceProvider, data.Owner);
                    await management.ChangePasswordAsync(new(password, "Synthetic-Replacement9!"), Cancellation);
                }
                else await management.LockAsync(data.Owner, Cancellation);
            }
            Assert.True(operation.Lifetime.IsCancellationRequested);
            if (phase == "inference") Assert.True(provider.InferenceCancellation.IsCancellationRequested);
            resume.TrySetResult();
            if (phase == "commit")
            {
                var result = await execution;
                await using var check = fixture.CreateAsyncScope();
                var stored = check.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(2, await stored.SuggestedProfileUpdates.CountAsync(row => row.InteractionAnalysisId == result.Id && row.Status == SuggestionStatus.Pending, Cancellation));
                Assert.False(await stored.PersonFacts.AnyAsync(row => row.PersonId == data.PersonId, Cancellation));
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
                Assert.Equal(phase == "snapshot" ? 0 : 1, provider.Calls);
                Assert.Equal(0, provider.Leases);
                await AssertHistoricalOnlyAsync(data);
            }
        }
        finally { resume.TrySetResult(); }
    }

    private async Task AssertHistoricalOnlyAsync(Seed data)
    {
        await using var verification = fixture.CreateAsyncScope();
        var stored = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await stored.InteractionAnalyses.CountAsync(analysis => analysis.InteractionId == data.InteractionId, Cancellation));
        Assert.Equal("Historical summary", await stored.InteractionAnalyses.Where(analysis => analysis.Id == data.HistoricalId).Select(analysis => analysis.Summary).SingleAsync(Cancellation));
        Assert.False(await stored.SuggestedProfileUpdates.AnyAsync(suggestion => suggestion.InteractionAnalysis.InteractionId == data.InteractionId, Cancellation));
        Assert.False(await stored.PersonFacts.AnyAsync(fact => fact.PersonId == data.PersonId, Cancellation));
        Assert.Equal("Original notes", await stored.People.Where(person => person.Id == data.PersonId).Select(person => person.Notes).SingleAsync(Cancellation));
    }

    private static async Task SwitchAsync(IServiceProvider services, Guid owner)
    {
        await using var switching = services.CreateAsyncScope();
        var selector = switching.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
        var before = await selector.GetAsync(Cancellation);
        await selector.SelectAsync(owner, before.Version, Cancellation);
    }

    [Fact]
    public async Task Organization_during_inference_preserves_context_fingerprint_and_valid_analysis()
    {
        var data = await SeedAsync();
        var observer = new CommitObserver();
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(observer)));
        await using var scope = application.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await BindAsync(services, data.Owner);
        var database = services.GetRequiredService<AppDbContext>(); observer.Target = database;
        var before = await services.GetRequiredService<AiContextBuilder>().BuildAsync(data.InteractionId, null, [], Cancellation);
        var provider = new ProbeProvider(database, observer)
        {
            DuringInference = async () =>
            {
                await using var writer = fixture.CreateAsyncScope();
                var writing = writer.ServiceProvider;
                await BindAsync(writing, data.Owner);
                await writing.GetRequiredService<SocialTelemetry.Application.Features.People.SetFavorite.Scenario>()
                    .ExecuteAsync(data.PersonId, true, Cancellation);
                var group = await writing.GetRequiredService<SocialTelemetry.Application.Features.PersonGroups.Create.Scenario>()
                    .ExecuteAsync("Not AI context", Cancellation);
                await writing.GetRequiredService<SocialTelemetry.Application.Features.PersonGroups.AddMember.Scenario>()
                    .ExecuteAsync(group.Id, data.PersonId, Cancellation);
                var after = await writing.GetRequiredService<AiContextBuilder>().BuildAsync(data.InteractionId, null, [], Cancellation);
                Assert.Equal(before.InputJson, after.InputJson);
                Assert.Equal(before.Fingerprint, after.Fingerprint);
            }
        };
        var result = await CreateScenario(services, provider).ExecuteAsync(new(data.InteractionId, null, []), Cancellation);
        Assert.Equal("synthetic-provider", result.Provider); Assert.Equal(1, provider.Calls); Assert.Equal(1, provider.Leases);
        Assert.Equal(2, await database.InteractionAnalyses.CountAsync(row => row.InteractionId == data.InteractionId, Cancellation));
        Assert.Equal(2, await database.SuggestedProfileUpdates.CountAsync(row => row.InteractionAnalysis.InteractionId == data.InteractionId, Cancellation));
    }

    private static AppAnalysis.Scenario CreateScenario(IServiceProvider services, IAiClient provider, IAnalysisPersistence? persistence = null) => new(
        persistence ?? services.GetRequiredService<IAnalysisPersistence>(), services.GetRequiredService<AiContextBuilder>(), provider,
        services.GetRequiredService<IOptions<AnalysisOptions>>().Value, services.GetRequiredService<LocalProfileOperation>());

    private static async Task BindAsync(IServiceProvider services, Guid owner)
    {
        var selector = services.GetRequiredService<LocalProfileSelectionService>();
        var selection = await selector.GetAsync(Cancellation);
        var binding = await selector.ResolveAsync(owner, selection.Version, Cancellation);
        await services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
            .ExecuteAsync(binding.UserProfileId, binding.SelectionVersion, Cancellation);
    }

    private async Task<Seed> SeedAsync()
    {
        var owner = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var person = new Person { Id = Guid.NewGuid(), UserProfileId = owner, DisplayName = "Synthetic participant", Notes = "Original notes" };
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = owner, Title = "Synthetic interaction",
            Description = "Synthetic evidence", OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            Participants = [new InteractionParticipant { PersonId = person.Id }] };
        var historical = new InteractionAnalysis { Id = Guid.NewGuid(), InteractionId = interaction.Id, Summary = "Historical summary",
            Provider = "legacy", Model = "legacy", SchemaVersion = "legacy", CreatedAt = DateTimeOffset.UtcNow };
        database.AddRange(person, interaction, historical);
        await database.SaveChangesAsync(Cancellation);
        return new(owner, person.Id, interaction.Id, historical.Id);
    }

    private sealed record Seed(Guid Owner, Guid PersonId, Guid InteractionId, Guid HistoricalId);
    private sealed class SyntheticCommitFailure : Exception;

    private sealed class BoundaryPersistence(IAnalysisPersistence inner) : IAnalysisPersistence
    {
        public Func<Task>? AfterSnapshot { get; init; }
        public Func<Task>? AfterCommit { get; init; }
        public CancellationToken SnapshotToken { get; private set; }

        public async Task<BuiltAnalysisContext> SnapshotAsync(Func<CancellationToken, Task<BuiltAnalysisContext>> build, CancellationToken cancellationToken)
        {
            SnapshotToken = cancellationToken;
            var result = await inner.SnapshotAsync(build, cancellationToken);
            if (AfterSnapshot is not null) await AfterSnapshot();
            return result;
        }

        public async Task<InteractionAnalysis> CommitAsync(Guid owner, Func<CancellationToken, Task> verifyContext,
            Func<CancellationToken, Task<IAiExecutionLease>> acquireExecutionLease, Func<AnalysisMutation> create, CancellationToken cancellationToken)
        {
            var result = await inner.CommitAsync(owner, verifyContext, acquireExecutionLease, create, cancellationToken);
            if (AfterCommit is not null) await AfterCommit();
            return result;
        }
    }

    private sealed class CommitObserver : DbTransactionInterceptor
    {
        public AppDbContext? Target { get; set; }
        public bool FailCommit { get; init; }
        public bool SnapshotCommitted { get; private set; }
        public bool LeaseActive { get; set; }
        public bool LeaseValidated { get; set; }
        public bool FinalCommitted { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context == Target && LeaseActive)
            {
                Assert.True(LeaseValidated);
                if (FailCommit) throw new SyntheticCommitFailure();
            }
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context == Target)
            {
                if (LeaseActive) FinalCommitted = true;
                else SnapshotCommitted = true;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ProbeProvider(AppDbContext database, CommitObserver observer) : IAiClient
    {
        public int Calls { get; private set; }
        public int Leases { get; private set; }
        public int DisposedLeases { get; private set; }
        public bool DisposedAfterCommit { get; private set; }
        public bool ValidatedAfterSqlSave { get; private set; }
        public bool FailLease { get; init; }
        public Func<Task>? DuringModel { get; init; }
        public Func<Task>? DuringInference { get; init; }
        public CancellationToken ModelCancellation { get; private set; }
        public CancellationToken InferenceCancellation { get; private set; }
        public CancellationToken LifetimeToken { get; init; }
        private Guid interactionId;

        public async Task<AiSelectedModel> GetSelectedModelAsync(CancellationToken cancellationToken)
        {
            ModelCancellation = cancellationToken;
            if (DuringModel is not null) await DuringModel();
            return new AiSelectedModel("synthetic-provider", "synthetic-model", [AiCapability.Text, AiCapability.StructuredOutput]);
        }

        public async Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken)
        {
            Assert.True(observer.SnapshotCommitted);
            Assert.Null(database.Database.CurrentTransaction);
            Assert.False(observer.LeaseActive);
            InferenceCancellation = cancellationToken;
            Calls++;
            var context = JsonSerializer.Deserialize<InteractionAnalysisContext>(request.Input, AiContextBuilder.JsonOptions)
                ?? throw new InvalidOperationException();
            interactionId = context.CurrentInteraction.Id;
            if (DuringInference is not null) await DuringInference();
            var person = Assert.Single(context.Participants).Id;
            var result = new InteractionAnalysisResult("Synthetic interpretation")
            {
                SuggestedProfileUpdates = [new ProposedUpdate(person, "Notes", "Proposed notes"), new ProposedUpdate(person, "Description", "Proposed description")]
            };
            return new(JsonSerializer.Serialize(result, AiContextBuilder.JsonOptions), "synthetic-provider", "synthetic-model", null)
            {
                LifetimeCancellationToken = LifetimeToken
            };
        }

        public Task ValidateExecutionAsync(AiTextResponse result, CancellationToken cancellationToken)
        {
            Assert.Null(database.Database.CurrentTransaction);
            return Task.CompletedTask;
        }

        public Task<IAiExecutionLease> AcquireExecutionLeaseAsync(AiTextResponse result, CancellationToken cancellationToken)
        {
            Assert.NotNull(database.Database.CurrentTransaction);
            Leases++;
            observer.LeaseActive = true;
            return Task.FromResult<IAiExecutionLease>(new Lease(this));
        }

        private sealed class Lease(ProbeProvider provider) : IAiExecutionLease
        {
            public async Task ValidateAsync(CancellationToken cancellationToken)
            {
                Assert.NotNull(provider.Database.Database.CurrentTransaction);
                var stored = provider.Database;
                Assert.True(await stored.InteractionAnalyses.AnyAsync(analysis => analysis.InteractionId == provider.interactionId && analysis.ResultJson != null, cancellationToken));
                Assert.Equal(2, await stored.SuggestedProfileUpdates.CountAsync(suggestion => suggestion.InteractionAnalysis.InteractionId == provider.interactionId && suggestion.Status == SuggestionStatus.Pending, cancellationToken));
                provider.ValidatedAfterSqlSave = true;
                provider.Observer.LeaseValidated = true;
                if (provider.FailLease) throw new AiProviderException(AiFailure.ExecutionInvalidated);
            }

            public void Dispose()
            {
                provider.DisposedLeases++;
                provider.DisposedAfterCommit = provider.Observer.FinalCommitted;
                provider.Observer.LeaseActive = false;
            }
        }

        private AppDbContext Database => database;
        private CommitObserver Observer => observer;
    }
}

public sealed class AnalysisApplicationTests(PeopleApiFixture fixture) : AnalysisApplicationTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteAnalysisApplicationTests(SqliteApiFixture fixture) : AnalysisApplicationTestsContract<SqliteApiFixture>(fixture);
