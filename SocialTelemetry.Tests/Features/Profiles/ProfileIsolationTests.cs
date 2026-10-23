using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Features.Analysis;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Application.Contracts.AI;
using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Infrastructure.Storage;
using SocialTelemetry.Infrastructure.Transcription;
using SocialTelemetry.Tests.Features.People;
using SocialTelemetry.Tests.Infrastructure.AI;
using SocialTelemetry.Application.Features.LocalAccess;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class ProfileIsolationTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Other_host_journal_deletion_blocks_original_callback_before_or_during_exchange(bool duringExchange)
    {
        var first = await SeedAsync();
        void DelayRefresh(IServiceCollection services)
        {
            foreach (var descriptor in services.Where(service => service.ImplementationType == typeof(LocalAccessExpirationService)).ToArray())
                services.Remove(descriptor);
        }
        using var host = Host(DelayRefresh);
        using var other = Host(DelayRefresh, host.CredentialDirectory);
        using var client = host.Client();
        using (var begin = await client.PostAsJsonAsync("/ai-connection/chatgpt/connect", new { }, Cancellation))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var callback = host.Callback();
        host.Server.AuthorizationExchangeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.ContinueAuthorizationExchange = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage>? completing = null;
        if (duringExchange)
        {
            completing = client.GetAsync(callback, Cancellation);
            await host.Server.AuthorizationExchangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        }
        try { await DeleteOpenedAsync(other, first.Owner); }
        finally { host.Server.ContinueAuthorizationExchange.TrySetResult(); }
        using var rejected = await (completing ?? client.GetAsync(callback, Cancellation));
        Assert.Contains(rejected.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict });
        Assert.Equal(duringExchange ? 1 : 0, host.Server.ExchangeCount);
        Assert.Equal(0, host.Server.RevokeCount);
        using var replay = await client.GetAsync(callback, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        var store = host.Application.Services.GetRequiredService<ChatGptCredentialStore>();
        using (await store.LockAsync(Cancellation))
            Assert.DoesNotContain((await store.ReadAsync(Cancellation)).Accounts, account => account.UserProfileId == first.Owner);
        await using var checking = fixture.CreateAsyncScope();
        Assert.False(await checking.ServiceProvider.GetRequiredService<AppDbContext>().Set<ProfileAiSelection>()
            .AnyAsync(selection => selection.UserProfileId == first.Owner, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Journal_deletion_cancels_unfinished_AI_or_STT_and_ignored_late_output_cannot_restore_the_graph(bool speech)
    {
        var first = await SeedAsync();
        var retained = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        var transcription = new PausedSpeech();
        using var host = Host(services =>
        {
            services.RemoveAll<ISpeechToTextClient>();
            services.AddSingleton<ISpeechToTextClient>(transcription);
        });
        using var client = host.Client();
        if (speech)
        {
            await using var remove = fixture.CreateAsyncScope();
            await remove.ServiceProvider.GetRequiredService<AppDbContext>().AttachmentTranscripts
                .Where(transcript => transcript.AttachmentId == first.Audio).ExecuteDeleteAsync(Cancellation);
        }
        else
        {
            await AdoptAsync(client, await LegacyAsync(host));
            host.Server.StreamBody = CompletedResult(first.Person);
            using var committed = await client.PostAsJsonAsync($"/interactions/{first.Interaction}/analyze", new { }, Cancellation);
            Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
            await AssertStoredAsync(first, 1); // A valid prior commit is successful before explicit deletion removes its history.
            host.Server.InferenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Server.ContinueInference = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Server.IgnoreInferenceCancellation = true;
        }
        var running = client.PostAsJsonAsync(speech ? $"/interactions/{first.Interaction}/attachments/{first.Audio}/transcribe" :
            $"/interactions/{first.Interaction}/analyze", new { }, Cancellation);
        await (speech ? transcription.Started.Task : host.Server.InferenceStarted!.Task).WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        try { await DeleteOpenedAsync(host, first.Owner); }
        finally
        {
            transcription.Continue.TrySetResult();
            host.Server.ContinueInference?.TrySetResult();
        }
        using var late = await running;
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        if (speech) Assert.True(transcription.Token.IsCancellationRequested);
        await using var checking = fixture.CreateAsyncScope();
        var database = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.UserProfiles.AnyAsync(profile => profile.Id == first.Owner, Cancellation));
        Assert.False(await database.AttachmentTranscripts.AnyAsync(transcript => transcript.AttachmentId == first.Audio, Cancellation));
        Assert.False(await database.InteractionAnalyses.AnyAsync(analysis => analysis.InteractionId == first.Interaction, Cancellation));
        Assert.True(await database.UserProfiles.AnyAsync(profile => profile.Id == retained.Owner, Cancellation));
        Assert.True(await database.PersonFacts.AnyAsync(fact => fact.Id == retained.Fact, Cancellation));
    }

    [Fact]
    public async Task Deletion_winning_before_adoption_protection_cannot_claim_an_unassigned_credential()
    {
        var first = await SeedAsync();
        var gate = new BeforeAdoption();
        using var host = Host(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate));
            foreach (var descriptor in services.Where(service => service.ImplementationType == typeof(LocalAccessExpirationService)).ToArray())
                services.Remove(descriptor);
        });
        using var other = Host(credentialDirectory: host.CredentialDirectory);
        using var client = host.Client();
        var unassigned = await LegacyAsync(host);
        gate.Armed = true;
        var claiming = client.PutAsJsonAsync("/ai-connection/chatgpt/connection", new { ConnectionId = unassigned, AdoptUnassigned = true }, Cancellation);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        try { await DeleteOpenedAsync(other, first.Owner); }
        finally { gate.Continue.TrySetResult(); }
        using var rejected = await claiming;
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var store = host.Application.Services.GetRequiredService<ChatGptCredentialStore>();
        using (await store.LockAsync(Cancellation))
        {
            var account = Assert.Single((await store.ReadAsync(Cancellation)).Accounts, account => account.Id == unassigned);
            Assert.Null(account.UserProfileId); Assert.NotNull(account.Tokens);
        }
    }

    private static async Task DeleteOpenedAsync(TestHost host, Guid owner)
    {
        await using var scope = host.Application.Services.CreateAsyncScope();
        var selection = await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        await scope.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
            .ExecuteAsync(owner, selection.Version, Cancellation);
        var result = await scope.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.LocalProfiles.Delete.Scenario>()
            .ExecuteAsync(owner, selection.Version, true, null, Cancellation);
        Assert.Null(result.UserProfileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_analysis_commit_wins_before_waiting_journal_deletion(bool otherHost)
    {
        var first = await SeedAsync();
        var commit = new AnalysisCommitGate();
        var response = new CommittedResponseGate();
        var attempt = new SelectionAttempt();
        using var host = Host(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(commit, attempt));
            ConfigureCommittedResponse(services, response);
        });
        using var other = otherHost ? Host(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(attempt)), host.CredentialDirectory) : null;
        using var startedOther = other?.Client(); // Finish host startup before holding the analysis writer gate.
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        host.Server.StreamBody = CompletedResult(first.Person);
        var analysis = client.PostAsJsonAsync($"/interactions/{first.Interaction}/analyze", new { }, Cancellation);
        Task? deleting = null;
        try
        {
            await commit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            attempt.Armed = true;
            deleting = Task.Run(() => DeleteOpenedAsync(other ?? host, first.Owner), Cancellation);
            await attempt.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            Assert.False(deleting.IsCompleted);
            commit.Continue.TrySetResult();
            await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            await deleting;
            Assert.True(commit.Committed);
            if (otherHost)
            {
                // Cooperating hosts refresh from authoritative SQL; there is no immediate IPC cancellation.
                Assert.False(response.OwnerLifetime.IsCancellationRequested);
                await using var refresh = host.Application.Services.CreateAsyncScope();
                await refresh.ServiceProvider.GetRequiredService<LocalAccessManagement>().ExpireAsync(Cancellation);
            }
            Assert.True(response.OwnerLifetime.IsCancellationRequested);
            response.Continue.TrySetResult();
            using var completed = await analysis;
            Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
            await using var checking = fixture.CreateAsyncScope();
            var database = checking.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await database.UserProfiles.AnyAsync(profile => profile.Id == first.Owner, Cancellation));
            Assert.False(await database.InteractionAnalyses.AnyAsync(row => row.InteractionId == first.Interaction, Cancellation));
            Assert.Equal(0, host.Server.RevokeCount);
        }
        finally
        {
            commit.Continue.TrySetResult();
            response.Continue.TrySetResult();
            if (deleting is not null) await deleting;
        }
    }

    [Fact]
    public async Task Caller_cancellation_after_analysis_commit_is_not_replaced_by_a_success_response()
    {
        var first = await SeedAsync();
        var response = new CommittedResponseGate();
        using var host = Host(services => ConfigureCommittedResponse(services, response));
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        host.Server.StreamBody = CompletedResult(first.Person);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var analysis = client.PostAsJsonAsync($"/interactions/{first.Interaction}/analyze", new { }, caller.Token);
        try
        {
            await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            await AssertStoredAsync(first, 1);
            caller.Cancel();
            response.Continue.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
            await response.Released.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            await AssertStoredAsync(first, 1);
        }
        finally { response.Continue.TrySetResult(); }
    }

    private static void ConfigureCommittedResponse(IServiceCollection services, CommittedResponseGate response)
    {
        // Refresh explicitly in the race test rather than depending on the maintenance timer.
        foreach (var descriptor in services.Where(service => service.ImplementationType == typeof(LocalAccessExpirationService)).ToArray())
            services.Remove(descriptor);
        services.RemoveAll<IAnalysisPersistence>();
        services.AddScoped<IAnalysisPersistence>(provider =>
        {
            var database = provider.GetRequiredService<AppDbContext>();
            return new CommittedResponsePersistence(new AnalysisExecutionPersistence(database), database,
                provider.GetRequiredService<LocalProfileOperation>(), response);
        });
    }

    private sealed class CommittedResponseGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken OwnerLifetime { get; set; }
    }

    private sealed class CommittedResponsePersistence(IAnalysisPersistence inner, AppDbContext database,
        LocalProfileOperation operation, CommittedResponseGate response) : IAnalysisPersistence
    {
        public Task<BuiltAnalysisContext> SnapshotAsync(Func<CancellationToken, Task<BuiltAnalysisContext>> build,
            CancellationToken cancellationToken) => inner.SnapshotAsync(build, cancellationToken);

        public async Task<InteractionAnalysis> CommitAsync(Guid owner, Func<CancellationToken, Task> verifyContext,
            Func<CancellationToken, Task<IAiExecutionLease>> acquireExecutionLease, Func<AnalysisMutation> create,
            CancellationToken cancellationToken)
        {
            var result = await inner.CommitAsync(owner, verifyContext, acquireExecutionLease, create, cancellationToken);
            Assert.Null(database.Database.CurrentTransaction);
            response.OwnerLifetime = operation.Lifetime;
            response.Entered.TrySetResult();
            // Intentionally pause only AFTER the real atomic commit and disposal of all protection.
            // Cancellation here must neither undo the committed result nor retarget the response.
            await response.Continue.Task;
            response.Released.TrySetResult();
            return result;
        }
    }

    private sealed class BeforeAdoption : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("ProfileAiSelections", StringComparison.Ordinal) && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal))
            {
                Armed = false;
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Bound_callback_checks_cross_host_admission_before_registration_and_after_exchange(bool duringExchange, bool expire, bool expireAfterGuard)
    {
        var data = await SeedAsync();
        // Deliberately withhold maintenance publication on the callback host. The SQL
        // checks must work even though its captured owner lifetime is still live.
        var clock = new LocalAccessClock();
        var publicationRead = new CallbackPublicationRead(() => clock.Advance(TimeSpan.FromDays(14)));
        void DelayRefresh(IServiceCollection services)
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(publicationRead));
            foreach (var descriptor in services.Where(service => service.ImplementationType == typeof(LocalAccessExpirationService)).ToArray())
                services.Remove(descriptor);
        }
        using var host = Host(DelayRefresh);
        using var client = host.Client();
        using var otherHost = Host(DelayRefresh, host.CredentialDirectory);
        using var otherClient = otherHost.Client();
        const string password = "Synthetic-Callback9!";
        var login = "cross-host-" + Guid.NewGuid().ToString("N");
        AccessChange enrolled;
        await using (var enrollment = host.Application.Services.CreateAsyncScope())
        {
            var selection = await enrollment.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
            await enrollment.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
                .ExecuteAsync(data.Owner, selection.Version, Cancellation);
            enrolled = await enrollment.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, password), Cancellation);
        }
        AccessChange signed;
        await using (var signing = host.Application.Services.CreateAsyncScope())
            signed = await signing.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .SignInAsync(new(login, password, data.Owner, enrolled.Selection.Version, true), Cancellation);

        async Task<CancellationToken> BeginAsync(Guid? connectionId)
        {
            await using var beginning = host.Application.Services.CreateAsyncScope();
            beginning.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
            await beginning.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
                .ExecuteAsync(data.Owner, signed.Selection.Version, Cancellation);
            await beginning.ServiceProvider.GetRequiredService<ProfileAiAccess>().BeginAsync(connectionId, connectionId is null, false, Cancellation);
            return beginning.ServiceProvider.GetRequiredService<LocalProfileOperation>().Lifetime;
        }
        async Task<ChatGptConnectionStatus> CompleteAsync()
        {
            var callback = QueryHelpers.ParseQuery(new Uri("http://127.0.0.1" + host.Callback()).Query);
            await using var completing = host.Application.Services.CreateAsyncScope();
            return await completing.ServiceProvider.GetRequiredService<ProfileAiAccess>().CompleteAsync(
                callback["state"], callback["code"], callback["client_id"], null, Cancellation);
        }

        Guid? connectionId = null;
        CancellationToken previousSessionLifetime = default;
        Guid? previousSession = null;
        var connection = host.Application.Services.GetRequiredService<ChatGptConnection>();
        if (duringExchange)
        {
            await BeginAsync(null);
            var connected = await CompleteAsync();
            Assert.True(connected.Connected);
            connectionId = Assert.IsType<Guid>(connected.ConnectionId);
            var session = await connection.GetOwnedInferenceSessionAsync(data.Owner, connectionId.Value, "gpt-5.6-sol", Cancellation);
            previousSession = session.SessionId;
            previousSessionLifetime = session.LifetimeCancellationToken;
        }
        var ownerLifetime = await BeginAsync(connectionId);
        var store = host.Application.Services.GetRequiredService<ChatGptCredentialStore>();
        ChatGptState before;
        using (await store.LockAsync(Cancellation)) before = await store.ReadAsync(Cancellation);
        var credentialFile = Path.Combine(host.CredentialDirectory, "chatgpt-state.bin");
        var beforeFileHash = SHA256.HashData(await File.ReadAllBytesAsync(credentialFile, Cancellation));
        Guid pendingGeneration;
        await using (var beforeCallback = fixture.CreateAsyncScope())
            pendingGeneration = await beforeCallback.ServiceProvider.GetRequiredService<AppDbContext>().Set<ProfileAiSelection>()
                .Where(selection => selection.UserProfileId == data.Owner).Select(selection => selection.Generation).SingleAsync(Cancellation);
        var exchangeCount = host.Server.ExchangeCount;
        var exchangeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExchange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (duringExchange)
        {
            host.Server.AuthorizationExchangeEntered = exchangeEntered;
            host.Server.ContinueAuthorizationExchange = releaseExchange;
        }
        Task<ChatGptConnectionStatus>? completion = null;
        if (duringExchange)
        {
            completion = CompleteAsync();
            await exchangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        }
        try
        {
            await using var revoking = otherHost.Application.Services.CreateAsyncScope();
            if (expireAfterGuard)
            {
                // The final SQL guard has admitted the operation; advance time only
                // after its selection query, immediately before credential publication.
                publicationRead.Armed = true;
            }
            else if (expire)
            {
                var database = revoking.ServiceProvider.GetRequiredService<AppDbContext>();
                await using var transaction = await database.Database.BeginTransactionAsync(Cancellation);
                await database.LockLocalProfileSelectionAsync(Cancellation);
                var grantId = (await database.Set<LocalProfileSelection>().AsNoTracking().SingleAsync(Cancellation)).AccessGrantId;
                var expiredAt = clock.GetUtcNow().AddSeconds(-1);
                await database.Set<LocalProfileGrant>().Where(grant => grant.Id == grantId)
                    .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.ExpiresAt, expiredAt), Cancellation);
                await transaction.CommitAsync(Cancellation);
            }
            else
            {
                revoking.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
                await revoking.ServiceProvider.GetRequiredService<ILocalAccessManagement>().LockAsync(data.Owner, Cancellation);
            }
            Assert.False(ownerLifetime.IsCancellationRequested);
        }
        finally { releaseExchange.TrySetResult(); }
        var failure = await Record.ExceptionAsync(async () => { await (completion ?? CompleteAsync()); });
        Assert.True(failure is LocalAccessException or LocalProfileAccessException or OperationCanceledException);
        Assert.Equal(exchangeCount + (duringExchange ? 1 : 0), host.Server.ExchangeCount);
        Assert.False(previousSessionLifetime.IsCancellationRequested);
        if (!duringExchange)
            Assert.Equal(beforeFileHash, SHA256.HashData(await File.ReadAllBytesAsync(credentialFile, Cancellation)));
        using (await store.LockAsync(Cancellation))
        {
            var after = await store.ReadAsync(Cancellation);
            Assert.Equal(before.Accounts.Count, after.Accounts.Count);
            Assert.Equal(before.ActiveConnectionId, after.ActiveConnectionId);
            if (connectionId is { } id)
            {
                var original = Assert.Single(before.Accounts, account => account.Id == id);
                var retained = Assert.Single(after.Accounts, account => account.Id == id);
                Assert.Equal(previousSession, retained.Tokens?.SessionId);
                Assert.True(original.Tokens?.AccessToken == retained.Tokens?.AccessToken);
                Assert.True(original.Subject == retained.Subject);
            }
        }
        var replay = await Assert.ThrowsAsync<AiProviderException>(() => CompleteAsync());
        Assert.Equal(AiFailure.InvalidCallback, replay.Failure);
        await using var checking = fixture.CreateAsyncScope();
        var selected = await checking.ServiceProvider.GetRequiredService<AppDbContext>().Set<ProfileAiSelection>()
            .AsNoTracking().SingleAsync(selection => selection.UserProfileId == data.Owner, Cancellation);
        Assert.Equal(connectionId, selected.ConnectionId);
        Assert.Equal(duringExchange, selected.Enabled);
        Assert.Equal(pendingGeneration, selected.Generation);
        if (expireAfterGuard) Assert.True(publicationRead.Triggered);
    }

    private sealed class CallbackPublicationRead(Action advance) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FROM \"ProfileAiSelections\"", StringComparison.Ordinal))
            {
                Armed = false;
                Triggered = true;
                advance();
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Protected_OAuth_callback_cannot_replace_revoked_begin_admission_with_current_device_or_owner()
    {
        var data = await SeedAsync();
        using var host = Host();
        using var client = host.Client();
        const string password = "Synthetic-Callback9!";
        var login = "callback-" + Guid.NewGuid().ToString("N");
        AccessChange enrolled;
        await using (var enrollment = host.Application.Services.CreateAsyncScope())
        {
            var selector = enrollment.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
            var selected = await selector.GetAsync(Cancellation);
            await enrollment.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
                .ExecuteAsync(data.Owner, selected.Version, Cancellation);
            enrolled = await enrollment.ServiceProvider.GetRequiredService<ILocalAccessManagement>().EnableAsync(new(login, password), Cancellation);
        }
        AccessChange signed;
        await using (var signing = host.Application.Services.CreateAsyncScope())
            signed = await signing.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .SignInAsync(new(login, password, data.Owner, enrolled.Selection.Version, true), Cancellation);
        await using (var beginning = host.Application.Services.CreateAsyncScope())
        {
            beginning.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
            await beginning.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
                .ExecuteAsync(data.Owner, signed.Selection.Version, Cancellation);
            await beginning.ServiceProvider.GetRequiredService<ProfileAiAccess>().BeginAsync(null, true, false, Cancellation);
        }
        var callback = host.Callback();
        await using (var locking = host.Application.Services.CreateAsyncScope())
        {
            locking.ServiceProvider.GetRequiredService<LocalAccessRequest>().SetEvidence(signed.DeviceEvidence!);
            await locking.ServiceProvider.GetRequiredService<ILocalAccessManagement>().LockAsync(data.Owner, Cancellation);
        }
        // A newly authenticated admission for the same owner cannot revive the old begin proof.
        await using (var signingAgain = host.Application.Services.CreateAsyncScope())
        {
            var selected = await signingAgain.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
            await signingAgain.ServiceProvider.GetRequiredService<ILocalAccessManagement>()
                .SignInAsync(new(login, password, data.Owner, selected.Version, true), Cancellation);
        }
        using var rejected = await client.GetAsync(callback, Cancellation);
        await CodeAsync(rejected, HttpStatusCode.Conflict, "ExecutionInvalidated");
        Assert.Equal(0, host.Server.ExchangeCount);
        await using var check = fixture.CreateAsyncScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().Set<ProfileAiSelection>()
            .AnyAsync(row => row.UserProfileId == data.Owner && row.Enabled, Cancellation));
    }

    [Fact]
    public async Task Selector_exposes_only_identities_and_CAS_assertions_never_override_current_owner()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        using var host = Host();
        using var original = host.Client();
        var before = await original.GetFromJsonAsync<SocialTelemetry.Api.Features.LocalProfiles.Get.Response>("/local-profiles", Cancellation);
        Assert.NotNull(before);
        using var created = await original.PostAsJsonAsync("/local-profiles", new { DisplayName = "New unprotected identity", ExpectedVersion = before.Version }, Cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(first.Owner, (await CurrentAsync(original)).UserProfileId);
        using var switched = await original.PutAsJsonAsync("/local-profiles/current", new { UserProfileId = second.Owner, ExpectedVersion = before.Version }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        using var stale = await original.PutAsJsonAsync($"/people/{first.Person}", new { DisplayName = "Old form" }, Cancellation);
        await CodeAsync(stale, HttpStatusCode.Conflict, "SelectionChanged");
        using var cas = await original.PutAsJsonAsync("/local-profiles/current", new { UserProfileId = first.Owner, ExpectedVersion = before.Version }, Cancellation);
        await CodeAsync(cas, HttpStatusCode.Conflict, "SelectionChanged");
        var selection = await CurrentAsync(original);
        SetOwner(original, second.Owner, selection.Version);
        using var foreignAssertion = new HttpRequestMessage(HttpMethod.Get, $"/people/{first.Person}");
        using var foreign = await original.SendAsync(foreignAssertion, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var forgedBody = await original.PostAsJsonAsync("/people", new { UserProfileId = first.Owner, DisplayName = "Forged owner" }, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, forgedBody.StatusCode);
        original.DefaultRequestHeaders.Remove(LocalProfileMiddleware.VersionHeader);
        using var missing = await original.GetAsync("/user-profile", Cancellation);
        await CodeAsync(missing, HttpStatusCode.Conflict, "AssertionRequired");
        using var list = await original.GetAsync("/local-profiles", Cancellation);
        var listText = await list.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("PRIVATE_JOURNAL", listText);
    }

    [Fact]
    public async Task Every_foreign_root_child_media_and_analysis_path_is_unavailable_without_mutation()
    {
        var first = await SeedAsync();
        var foreign = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        using var host = Host();
        using var client = host.Client();
        var paths = new[]
        {
            $"/people/{foreign.Person}", $"/PeOpLe/{foreign.Person}", $"/people/{foreign.Person}/facts",
            $"/people/{foreign.Person}/facts/{foreign.Fact}", $"/people/{foreign.Person}/external-connections",
            $"/people/{foreign.Person}/avatar", $"/people/{foreign.Person}/interactions", $"/interactions/{foreign.Interaction}",
            $"/interactions/{foreign.Interaction}/attachments/{foreign.Audio}",
            $"/interactions/{foreign.Interaction}/attachments/{foreign.Audio}/content",
            $"/interactions/{foreign.Interaction}/attachments/{foreign.Audio}/transcript"
        };
        foreach (var path in paths)
        {
            using var read = await client.GetAsync(path, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            Assert.DoesNotContain("PRIVATE_JOURNAL", await read.Content.ReadAsStringAsync(Cancellation));
        }
        foreach (var path in new[] { $"/people/{foreign.Person}", $"/people/{foreign.Person}/facts/{foreign.Fact}",
                     $"/people/{foreign.Person}/avatar", $"/interactions/{foreign.Interaction}",
                     $"/interactions/{foreign.Interaction}/attachments/{foreign.Audio}" })
        {
            using var deleted = await client.DeleteAsync(path, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }
        foreach (var suffix in new[] { "analyze", $"attachments/{foreign.Audio}/transcribe" })
        {
            using var execution = await client.PostAsJsonAsync($"/interactions/{foreign.Interaction}/{suffix}", new { }, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, execution.StatusCode);
        }
        using var childMismatch = await client.GetAsync($"/interactions/{first.Interaction}/attachments/{foreign.Audio}/transcript", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, childMismatch.StatusCode);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await database.People.AnyAsync(person => person.Id == foreign.Person, Cancellation));
        Assert.True(await database.AttachmentTranscripts.AnyAsync(transcript => transcript.AttachmentId == foreign.Audio, Cancellation));
        Assert.Equal(0, host.Server.InferenceCount);
    }

    [Fact]
    public async Task Linked_account_cannot_be_opened_through_selector_or_asserted_directly()
    {
        var first = await SeedAsync();
        var protectedOwner = await fixture.CreateUserProfileAsync("Protected identity", select: false);
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.Users.Add(new ApplicationUser { UserProfileId = protectedOwner });
            await database.SaveChangesAsync(Cancellation);
        }
        await fixture.SelectProfileAsync(first.Owner);
        using var host = Host();
        using var client = host.Client();
        var selection = await CurrentAsync(client);
        using var denied = await client.PutAsJsonAsync("/local-profiles/current", new { UserProfileId = protectedOwner, ExpectedVersion = selection.Version }, Cancellation);
        await CodeAsync(denied, HttpStatusCode.Forbidden, "ProtectedAccessUnavailable");
        SetOwner(client, protectedOwner, selection.Version);
        using var forged = await client.GetAsync("/user-profile", Cancellation);
        await CodeAsync(forged, HttpStatusCode.Conflict, "SelectionChanged");
    }

    [Fact]
    public async Task Legacy_connections_require_explicit_exclusive_adoption_and_models_are_per_owner()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        using var host = Host();
        using var client = host.Client();
        var firstConnection = await LegacyAsync(host);
        var secondConnection = await LegacyAsync(host);
        Assert.False((await StatusAsync(client)).Connected);
        using var implicitClaim = await client.PutAsJsonAsync("/ai-connection/chatgpt/connection", new { ConnectionId = firstConnection }, Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, implicitClaim.StatusCode);
        await AdoptAsync(client, firstConnection);
        Assert.Equal(firstConnection, (await StatusAsync(client)).ConnectionId);
        Guid firstGeneration;
        await using (var selectionScope = fixture.CreateAsyncScope())
            firstGeneration = (await selectionScope.ServiceProvider.GetRequiredService<AppDbContext>().Set<ProfileAiSelection>()
                .SingleAsync(selection => selection.UserProfileId == first.Owner, Cancellation)).Generation;
        var originalAI = host.Application.Services.GetRequiredService<ProfileAiLifetimes>().Capture(first.Owner, firstGeneration);
        await SwitchAsync(client, second.Owner);
        Assert.False((await StatusAsync(client)).Connected);
        using var stolen = await client.PutAsJsonAsync("/ai-connection/chatgpt/connection", new { ConnectionId = firstConnection, AdoptUnassigned = true }, Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, stolen.StatusCode);
        var unassigned = await client.GetFromJsonAsync<ChatGptAccountSummary[]>("/ai-connection/chatgpt/unassigned", Cancellation);
        Assert.NotNull(unassigned);
        Assert.Contains(unassigned, account => account.Id == secondConnection);
        Assert.DoesNotContain(unassigned, account => account.Id == firstConnection);
        await AdoptAsync(client, secondConnection);
        using var model = await client.PutAsJsonAsync("/ai-connection/chatgpt/model", new { ModelId = "gpt-5.6" }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, model.StatusCode);
        using var disconnected = await client.DeleteAsync("/ai-connection/chatgpt", Cancellation);
        Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);
        Assert.False(originalAI.IsCancellationRequested);
        await SwitchAsync(client, first.Owner);
        Assert.Equal("gpt-5.6-sol", (await StatusAsync(client)).SelectedModel);
        Assert.True((await StatusAsync(client)).Connected);
        await using var scope = fixture.CreateAsyncScope();
        var selections = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<ProfileAiSelection>().AsNoTracking()
            .Where(selection => selection.UserProfileId == first.Owner || selection.UserProfileId == second.Owner).ToListAsync(Cancellation);
        Assert.Equal(2, selections.Count);
        Assert.Equal(2, selections.Select(selection => selection.ConnectionId).Distinct().Count());
        Assert.Equal(firstGeneration, selections.Single(selection => selection.UserProfileId == first.Owner).Generation);
        using var restarted = Host(credentialDirectory: host.CredentialDirectory);
        using var restartedClient = restarted.Client();
        var remembered = await CurrentAsync(restartedClient);
        Assert.Equal(first.Owner, remembered.UserProfileId);
        SetOwner(restartedClient, first.Owner, remembered.Version);
        var resumed = await StatusAsync(restartedClient);
        Assert.True(resumed.Connected);
        Assert.Equal(firstConnection, resumed.ConnectionId);
        Assert.Equal("gpt-5.6-sol", resumed.SelectedModel);
    }

    [Fact]
    public async Task Concurrent_protected_claims_have_one_owner_and_SQL_rejects_duplicate_or_dangling_mappings()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        using var host = Host();
        using var client = host.Client();
        var connectionId = await LegacyAsync(host);
        var connection = host.Application.Services.GetRequiredService<ChatGptConnection>();
        var claims = await Task.WhenAll(TryClaimAsync(first.Owner), TryClaimAsync(second.Owner));
        Assert.Single(claims, success => success);
        var winner = claims[0] ? first.Owner : second.Owner;
        var store = host.Application.Services.GetRequiredService<ChatGptCredentialStore>();
        using (await store.LockAsync(Cancellation))
            Assert.Equal(winner, Assert.Single((await store.ReadAsync(Cancellation)).Accounts).UserProfileId);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.Set<ProfileAiSelection>().Add(new() { UserProfileId = first.Owner, ConnectionId = connectionId });
        await database.SaveChangesAsync(Cancellation);
        database.Set<ProfileAiSelection>().Add(new() { UserProfileId = second.Owner, ConnectionId = connectionId });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));
        database.ChangeTracker.Clear();
        database.Set<ProfileAiSelection>().Add(new() { UserProfileId = Guid.NewGuid() });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));

        async Task<bool> TryClaimAsync(Guid owner)
        {
            try { await connection.ClaimAsync(owner, connectionId, true, Cancellation); return true; }
            catch (AiProviderException exception) when (exception.Failure == AiFailure.NotConnected) { return false; }
        }
    }

    [Fact]
    public async Task Implicit_profile_and_owned_parent_routes_do_not_allow_foreign_child_updates_or_deletes()
    {
        var first = await SeedAsync();
        var foreign = await SeedAsync();
        var personLink = Guid.NewGuid();
        var profileLink = Guid.NewGuid();
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.PersonExternalConnections.Add(new() { Id = personLink, PersonId = foreign.Person, Platform = "example", Handle = "PRIVATE_JOURNAL foreign" });
            database.UserProfileExternalConnections.Add(new() { Id = profileLink, UserProfileId = foreign.Owner, Platform = "example", Handle = "PRIVATE_JOURNAL foreign" });
            // Imported/legacy inconsistent links must not expose another profile's person.
            database.InteractionParticipants.Add(new() { InteractionId = first.Interaction, PersonId = foreign.Person });
            await database.SaveChangesAsync(Cancellation);
        }
        await fixture.SelectProfileAsync(first.Owner);
        using var host = Host();
        using var client = host.Client();
        using var profile = await client.GetAsync("/user-profile", Cancellation);
        var identity = await profile.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal(first.Owner, identity.GetProperty("id").GetGuid());
        using var interaction = await client.GetAsync($"/interactions/{first.Interaction}", Cancellation);
        var interactionBody = await interaction.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.DoesNotContain(interactionBody.GetProperty("participants").EnumerateArray(), person => person.GetProperty("id").GetGuid() == foreign.Person);
        foreach (var path in new[] { $"/people/{first.Person}/facts/{foreign.Fact}",
                     $"/people/{first.Person}/external-connections/{personLink}", $"/user-profile/external-connections/{profileLink}" })
        {
            using var changed = await client.PutAsJsonAsync(path, new { Value = "Forged change", Platform = "example", Handle = "Forged change" }, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, changed.StatusCode);
            using var deleted = await client.DeleteAsync(path, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }
        using var transcript = await client.PutAsJsonAsync($"/interactions/{first.Interaction}/attachments/{foreign.Audio}/transcript",
            new { CorrectedText = "Forged change", ConfirmReviewed = true }, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, transcript.StatusCode);
        using var participants = await client.PutAsJsonAsync($"/interactions/{first.Interaction}",
            new { Title = "Owned", Description = "Owned", OccurredAt = DateTimeOffset.UtcNow, ParticipantIds = new[] { foreign.Person } }, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, participants.StatusCode);
        await using var verification = fixture.CreateAsyncScope();
        var stored = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("PRIVATE_JOURNAL foreign", (await stored.PersonExternalConnections.SingleAsync(link => link.Id == personLink, Cancellation)).Handle);
        Assert.Equal("Synthetic confirmed fact", (await stored.PersonFacts.SingleAsync(fact => fact.Id == foreign.Fact, Cancellation)).Value);
    }

    [Fact]
    public async Task Begin_conflict_does_not_replace_pending_owner_and_switch_rejects_its_callback()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        using var host = Host();
        using var client = host.Client();
        using var begin = await client.PostAsJsonAsync("/ai-connection/chatgpt/connect", new { }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var callback = host.Callback();
        using var duplicate = await client.PostAsJsonAsync("/ai-connection/chatgpt/connect", new { }, Cancellation);
        await CodeAsync(duplicate, HttpStatusCode.Conflict, "ConnectionBusy");
        await SwitchAsync(client, second.Owner);
        using var late = await client.GetAsync(callback, Cancellation);
        await CodeAsync(late, HttpStatusCode.Conflict, "ExecutionInvalidated");
        Assert.False((await StatusAsync(client)).Connected);
        using var replay = await client.GetAsync(callback, Cancellation);
        await CodeAsync(replay, HttpStatusCode.BadRequest, "InvalidCallback");
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("model")]
    [InlineData("connection")]
    [InlineData("other-host-switch")]
    public async Task Late_inference_cannot_commit_after_original_binding_is_invalidated(string change)
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        using var host = Host();
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        var alternative = await LegacyAsync(host);
        host.Server.StreamBody = CompletedResult(first.Person);
        using (var success = await client.PostAsJsonAsync($"/interactions/{first.Interaction}/analyze", new { }, Cancellation))
            Assert.Equal(HttpStatusCode.Created, success.StatusCode);
        host.Server.InferenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.ContinueInference = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.IgnoreInferenceCancellation = true;
        var running = client.PostAsJsonAsync($"/interactions/{first.Interaction}/analyze", new { }, Cancellation);
        await host.Server.InferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        try
        {
            if (change == "switch") await SwitchAsync(client, second.Owner);
            if (change == "other-host-switch") await fixture.SelectProfileAsync(second.Owner);
            if (change == "connection") await AdoptAsync(client, alternative);
            if (change == "model")
            {
                using var changed = await client.PutAsJsonAsync("/ai-connection/chatgpt/model", new { ModelId = "gpt-5.6" }, Cancellation);
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            }
        }
        finally { host.Server.ContinueInference.TrySetResult(); }
        using var late = await running;
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await database.InteractionAnalyses.CountAsync(analysis => analysis.InteractionId == first.Interaction, Cancellation));
        Assert.Equal(1, await database.SuggestedProfileUpdates.CountAsync(suggestion => suggestion.InteractionAnalysis.InteractionId == first.Interaction, Cancellation));
        Assert.False(await database.InteractionAnalyses.AnyAsync(analysis => analysis.InteractionId == second.Interaction, Cancellation));
        if (change == "other-host-switch")
        {
            var current = await CurrentAsync(client);
            SetOwner(client, second.Owner, current.Version);
            using var refreshed = await client.GetAsync("/user-profile", Cancellation);
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            Assert.Equal(second.Owner, (await refreshed.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("id").GetGuid());
        }
    }

    [Fact]
    public async Task Switch_during_STT_rejects_late_result_without_a_transcript()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await using (var remove = fixture.CreateAsyncScope())
            await remove.ServiceProvider.GetRequiredService<AppDbContext>().AttachmentTranscripts.Where(transcript => transcript.AttachmentId == first.Audio).ExecuteDeleteAsync(Cancellation);
        await fixture.SelectProfileAsync(first.Owner);
        var speech = new PausedSpeech();
        using var host = Host(services => { services.RemoveAll<ISpeechToTextClient>(); services.AddSingleton<ISpeechToTextClient>(speech); });
        using var client = host.Client();
        var running = client.PostAsJsonAsync($"/interactions/{first.Interaction}/attachments/{first.Audio}/transcribe", new { }, Cancellation);
        await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        try { await SwitchAsync(client, second.Owner); }
        finally { speech.Continue.TrySetResult(); }
        using var late = await running;
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.True(speech.Token.IsCancellationRequested);
        await using var scope = fixture.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AttachmentTranscripts.AnyAsync(transcript => transcript.AttachmentId == first.Audio, Cancellation));
    }

    [Theory]
    [InlineData("adoption")]
    [InlineData("callback")]
    public async Task SQL_publication_failure_preserves_exclusive_protected_ownership_without_enabling_a_new_selection(string phase)
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        var failure = new FailSelectionSave();
        using var host = Host(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(failure)));
        using var client = host.Client();
        Guid connectionId;
        if (phase == "adoption")
        {
            connectionId = await LegacyAsync(host);
            failure.Armed = true;
            using var failed = await client.PutAsJsonAsync("/ai-connection/chatgpt/connection", new { ConnectionId = connectionId, AdoptUnassigned = true }, Cancellation);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        else
        {
            using var begin = await client.PostAsJsonAsync("/ai-connection/chatgpt/connect", new { }, Cancellation);
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            failure.Armed = true;
            using var failed = await client.GetAsync(host.Callback(), Cancellation);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            var store = host.Application.Services.GetRequiredService<ChatGptCredentialStore>();
            using var held = await store.LockAsync(Cancellation);
            var state = await store.ReadAsync(Cancellation);
            connectionId = Assert.Single(state.Accounts, account => account.UserProfileId == first.Owner).Id;
        }
        Assert.False((await StatusAsync(client)).Connected);
        await SwitchAsync(client, second.Owner);
        using var stolen = await client.PutAsJsonAsync("/ai-connection/chatgpt/connection", new { ConnectionId = connectionId, AdoptUnassigned = true }, Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, stolen.StatusCode);
        await SwitchAsync(client, first.Owner);
        await AdoptAsync(client, connectionId);
        Assert.True((await StatusAsync(client)).Connected);
    }

    [Fact]
    public async Task Failed_credential_file_disconnect_stays_disabled_after_restart_without_losing_protected_credentials()
    {
        var owner = await SeedAsync();
        using var host = Host();
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        var file = Path.Combine(host.CredentialDirectory, "chatgpt-state.bin");
        var saved = file + ".fixture-saved";
        var original = await File.ReadAllBytesAsync(file, Cancellation);
        File.Move(file, saved);
        Directory.CreateDirectory(file);
        try
        {
            using var failed = await client.DeleteAsync("/ai-connection/chatgpt", Cancellation);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.DoesNotContain(host.CredentialDirectory, await failed.Content.ReadAsStringAsync(Cancellation));
        }
        finally { Directory.Delete(file); File.Move(saved, file); }
        Assert.Equal(original, await File.ReadAllBytesAsync(file, Cancellation));
        using var restarted = Host(credentialDirectory: host.CredentialDirectory);
        using var restartedClient = restarted.Client();
        Assert.False((await StatusAsync(restartedClient)).Connected);
        using var analysis = await restartedClient.PostAsJsonAsync($"/interactions/{owner.Interaction}/analyze", new { }, Cancellation);
        await CodeAsync(analysis, HttpStatusCode.Conflict, "NotConnected");
        Assert.Equal(0, restarted.Server.InferenceCount);
    }

    [Theory]
    [InlineData("switch", false)]
    [InlineData("switch", true)]
    [InlineData("model", false)]
    [InlineData("model", true)]
    [InlineData("disconnect", false)]
    [InlineData("disconnect", true)]
    public async Task Guarded_analysis_commit_wins_before_waiting_switch_or_AI_change(string change, bool otherHost)
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        var commit = new AnalysisCommitGate();
        var attempt = new SelectionAttempt();
        using var host = Host(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(commit, attempt));
            services.AddSingleton<IStartupFilter>(attempt);
        });
        using var remoteHost = otherHost ? Host(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(attempt));
            services.AddSingleton<IStartupFilter>(attempt);
        }, host.CredentialDirectory) : null;
        using var client = host.Client();
        using var changingClient = (remoteHost ?? host).Client();
        await AdoptAsync(client, await LegacyAsync(host));
        host.Server.StreamBody = CompletedResult(first.Person);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var analysis = client.PostAsJsonAsync($"/interactions/{first.Interaction}/analyze", new { }, deadline.Token);
        Task? changing = null;
        try
        {
            await commit.Entered.Task.WaitAsync(deadline.Token);
            attempt.Armed = true;
            // SQLite's BEGIN IMMEDIATE is synchronous; put its writer wait on a test worker.
            changing = Task.Run(() => ChangeAsync(changingClient, change, second.Owner), deadline.Token);
            await attempt.Entered.Task.WaitAsync(deadline.Token);
            Assert.False(changing.IsCompleted);
            Assert.Equal(0, (remoteHost ?? host).Server.RevokeCount);
            await AssertStoredAsync(first, 0);
            commit.Continue.TrySetResult();
            using var response = await analysis;
            await changing;
            // Cancellation can win HTTP delivery after the SQL commit. It cannot erase or
            // retarget the already committed valid analysis and its Pending suggestion.
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            await AssertStoredAsync(first, 1);
            await AssertStoredAsync(second, 0);
            if (change == "disconnect") Assert.False((await StatusAsync(changingClient)).Connected);
        }
        finally
        {
            commit.Continue.TrySetResult();
            if (changing is not null) await changing;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_catalog_new_model_remains_selectable_and_owned_while_unknown_capabilities_block_inference(bool configured)
    {
        var owner = await SeedAsync();
        await fixture.SelectProfileAsync(owner.Owner);
        var configuration = new Dictionary<string, string?>
        {
            ["ChatGpt:CompatibilityProfiles:0:ModelId"] = "provider-new-model",
            ["ChatGpt:CompatibilityProfiles:0:Text"] = "Supported"
        };
        if (configured)
        {
            configuration["ChatGpt:CompatibilityProfiles:0:Vision"] = "Supported";
            configuration["ChatGpt:CompatibilityProfiles:0:StructuredOutput"] = "Supported";
        }
        using var host = Host(configuration: configuration);
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        host.Server.ModelsBody = """{"models":[{"slug":"provider-new-model","display_name":"Provider new model","visibility":"list","supports_json_schema":true},{"slug":"gpt-5.6-sol","display_name":"Existing","visibility":"list"}]}""";
        var catalog = await client.GetFromJsonAsync<JsonElement>("/ai-connection/chatgpt/models", Cancellation);
        Assert.Equal(new[] { "provider-new-model", "gpt-5.6-sol" }, catalog.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("id").GetString()));
        var capabilities = catalog.GetProperty("models")[0].GetProperty("capabilitySupport");
        Assert.Equal("Supported", capabilities.GetProperty("text").GetString());
        Assert.Equal(configured ? "Supported" : "Unknown", capabilities.GetProperty("vision").GetString());
        Assert.Equal(configured ? "Supported" : "Unknown", capabilities.GetProperty("structuredOutput").GetString());
        using (var selection = await client.PutAsJsonAsync("/ai-connection/chatgpt/model", new { ModelId = "provider-new-model" }, Cancellation))
            Assert.Equal(HttpStatusCode.OK, selection.StatusCode);
        await using (var scope = host.Application.Services.CreateAsyncScope())
        {
            await BindCurrentOperationAsync(scope.ServiceProvider, client);
            var selected = await scope.ServiceProvider.GetRequiredService<IAiClient>().GetSelectedModelAsync(Cancellation);
            Assert.Equal("provider-new-model", selected.ModelId);
            Assert.Equal(owner.Owner, selected.Binding?.UserProfileId);
            Assert.Equal(configured, selected.Supports(AiCapability.StructuredOutput));
        }
        host.Server.StreamBody = CompletedResult(owner.Person);
        using var analysis = await client.PostAsJsonAsync($"/interactions/{owner.Interaction}/analyze", new { }, Cancellation);
        if (configured)
        {
            Assert.Equal(HttpStatusCode.Created, analysis.StatusCode);
            using var payload = JsonDocument.Parse(host.Server.LastInferenceBody!);
            Assert.Equal("provider-new-model", payload.RootElement.GetProperty("model").GetString());
            await AssertStoredAsync(owner, 1);
        }
        else
        {
            await CodeAsync(analysis, HttpStatusCode.Conflict, "CapabilityMissing");
            var problem = await analysis.Content.ReadAsStringAsync(Cancellation);
            Assert.Contains("confirmed support", problem);
            Assert.Equal(0, host.Server.InferenceCount);
            Assert.Null(host.Server.LastInferenceBody);
            await AssertStoredAsync(owner, 0);
        }
        Assert.Equal("provider-new-model", (await StatusAsync(client)).SelectedModel);
        using var restarted = Host(credentialDirectory: host.CredentialDirectory);
        using var restartedClient = restarted.Client();
        Assert.Equal("provider-new-model", (await StatusAsync(restartedClient)).SelectedModel);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("model")]
    [InlineData("disconnect")]
    public async Task Change_winning_before_final_protection_rejects_a_completed_but_delayed_provider_result(string change)
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await fixture.SelectProfileAsync(first.Owner);
        var returned = new CompletedResultGate();
        using var host = Host(services =>
        {
            services.RemoveAll<IAiClient>();
            services.AddScoped<IAiClient>(provider => new DelayedCompletedClient(new ChatGptPlanAiClient(
                provider.GetRequiredService<ChatGptConnection>(), provider.GetRequiredService<ChatGptHttpClient>(),
                provider.GetRequiredService<ProfileAiAccess>(), provider.GetRequiredService<ChatGptCapabilityResolver>()), returned));
        });
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        host.Server.StreamBody = CompletedResult(first.Person);
        var running = client.PostAsJsonAsync($"/interactions/{first.Interaction}/analyze", new { }, Cancellation);
        await returned.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        try { await ChangeAsync(client, change, second.Owner); }
        finally { returned.Continue.TrySetResult(); }
        using var response = await running;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertStoredAsync(first, 0);
        await AssertStoredAsync(second, 0);
    }

    [Fact]
    public async Task Caller_cancellation_at_guarded_commit_rolls_back_and_releases_SQL_and_credential_protection()
    {
        var first = await SeedAsync();
        var commit = new AnalysisCommitGate();
        using var host = Host(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(commit)));
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        host.Server.StreamBody = CompletedResult(first.Person);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var analysis = client.PostAsJsonAsync($"/interactions/{first.Interaction}/analyze", new { }, caller.Token);
        try
        {
            await commit.Entered.Task.WaitAsync(deadline.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
            using var disconnected = await client.DeleteAsync("/ai-connection/chatgpt", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);
            await AssertStoredAsync(first, 0);
        }
        finally { commit.Continue.TrySetResult(); }
    }

    private async Task AssertStoredAsync(Seed owner, int count)
    {
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(count, await database.InteractionAnalyses.CountAsync(analysis => analysis.InteractionId == owner.Interaction, Cancellation));
        Assert.Equal(count, await database.SuggestedProfileUpdates.CountAsync(suggestion =>
            suggestion.InteractionAnalysis.InteractionId == owner.Interaction && suggestion.PersonId == owner.Person && suggestion.Status == SuggestionStatus.Pending, Cancellation));
    }

    private static async Task ChangeAsync(HttpClient client, string change, Guid destination)
    {
        if (change == "switch") { await SwitchAsync(client, destination); return; }
        using var response = change == "model"
            ? await client.PutAsJsonAsync("/ai-connection/chatgpt/model", new { ModelId = "gpt-5.6" }, Cancellation)
            : await client.DeleteAsync("/ai-connection/chatgpt", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_capture_or_post_change_Begin_cannot_cancel_new_generation_analysis(bool begin)
    {
        var owner = await SeedAsync();
        var barrier = new SelectionReadBarrier { ReadNumber = begin ? 3 : 1 };
        using var host = Host(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(barrier)));
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        await using var oldScope = host.Application.Services.CreateAsyncScope();
        var oldAccess = await BindCurrentOperationAsync(oldScope.ServiceProvider, client);
        barrier.Target = oldScope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        Task oldCapture = begin ? oldAccess.BeginAsync(null, false, false, deadline.Token) : oldAccess.CaptureAsync(deadline.Token);
        Task<HttpResponseMessage>? analysis = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(deadline.Token);
            using var changed = await client.PutAsJsonAsync("/ai-connection/chatgpt/model", new { ModelId = "gpt-5.6" }, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            await using var currentScope = host.Application.Services.CreateAsyncScope();
            var currentAccess = await BindCurrentOperationAsync(currentScope.ServiceProvider, client);
            var current = await currentAccess.CaptureAsync(deadline.Token);
            Assert.NotEqual(barrier.ObservedGeneration, current.Binding.SelectionGeneration);
            host.Server.StreamBody = CompletedResult(owner.Person);
            host.Server.InferenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Server.ContinueInference = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Server.IgnoreInferenceCancellation = true;
            analysis = client.PostAsJsonAsync($"/interactions/{owner.Interaction}/analyze", new { }, deadline.Token);
            await host.Server.InferenceStarted.Task.WaitAsync(deadline.Token);
            barrier.Continue.TrySetResult();
            var rejected = await Assert.ThrowsAsync<AiProviderException>(() => oldCapture);
            Assert.Equal(AiFailure.ExecutionInvalidated, rejected.Failure);
            Assert.False(current.Lifetime.IsCancellationRequested);
            Assert.Null(host.Browser.LastUri);
            host.Server.ContinueInference.TrySetResult();
            using var response = await analysis;
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.False(current.Lifetime.IsCancellationRequested);
            await AssertStoredAsync(owner, 1);
        }
        finally
        {
            barrier.Continue.TrySetResult();
            host.Server.ContinueInference?.TrySetResult();
            if (analysis is not null) using (await analysis) { }
        }
    }

    [Fact]
    public async Task Authoritative_capture_refreshes_another_hosts_generation_without_reviving_stale_binding()
    {
        var owner = await SeedAsync();
        using var host = Host();
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        await using var scope = host.Application.Services.CreateAsyncScope();
        var access = await BindCurrentOperationAsync(scope.ServiceProvider, client);
        var original = await access.CaptureAsync(Cancellation);
        using var otherHost = Host(credentialDirectory: host.CredentialDirectory);
        using var otherClient = otherHost.Client();
        using var changed = await otherClient.PutAsJsonAsync("/ai-connection/chatgpt/model", new { ModelId = "gpt-5.6" }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var current = await access.CaptureAsync(Cancellation);
        Assert.NotEqual(original.Binding.SelectionGeneration, current.Binding.SelectionGeneration);
        Assert.True(original.Lifetime.IsCancellationRequested);
        Assert.False(current.Lifetime.IsCancellationRequested);
        var stale = await Assert.ThrowsAsync<AiProviderException>(() => access.ValidateAsync(original.Binding, Cancellation));
        Assert.Equal(AiFailure.ExecutionInvalidated, stale.Failure);
        host.Server.StreamBody = CompletedResult(owner.Person);
        using var response = await client.PostAsJsonAsync($"/interactions/{owner.Interaction}/analyze", new { }, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await AssertStoredAsync(owner, 1);
    }

    [Fact]
    public async Task Generation_cancellation_callbacks_run_after_publication_coordination_is_released()
    {
        await SeedAsync();
        using var host = Host();
        using var client = host.Client();
        await AdoptAsync(client, await LegacyAsync(host));
        await using var scope = host.Application.Services.CreateAsyncScope();
        var access = await BindCurrentOperationAsync(scope.ServiceProvider, client);
        var original = await access.CaptureAsync(Cancellation);
        var lifetimes = host.Application.Services.GetRequiredService<ProfileAiLifetimes>();
        Task? reentry = null;
        var enteredImmediately = false;
        using var callback = original.Lifetime.Register(() =>
        {
            reentry = lifetimes.Changes.WaitAsync(Cancellation);
            enteredImmediately = reentry.IsCompletedSuccessfully;
        });
        try
        {
            using var changed = await client.PutAsJsonAsync("/ai-connection/chatgpt/model", new { ModelId = "gpt-5.6" }, Cancellation);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.True(enteredImmediately);
            Assert.True(original.Lifetime.IsCancellationRequested);
        }
        finally
        {
            if (reentry is not null)
            {
                await reentry;
                lifetimes.Changes.Release();
            }
        }
    }

    private static async Task<ProfileAiAccess> BindCurrentOperationAsync(IServiceProvider services, HttpClient client)
    {
        var selected = await CurrentAsync(client);
        var owner = Assert.IsType<Guid>(selected.UserProfileId);
        await services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
            .ExecuteAsync(owner, selected.Version, Cancellation);
        return services.GetRequiredService<ProfileAiAccess>();
    }

    private async Task<Seed> SeedAsync()
    {
        var owner = await fixture.CreateUserProfileAsync("Local identity");
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        byte[] audio = [1, 2, 3, 4];
        using var stream = new MemoryStream(audio);
        var key = await storage.SaveAsync(stream, "synthetic.wav", "audio/wav", Cancellation);
        await storage.CompleteUploadAsync(key, Cancellation);
        var person = new Person { Id = Guid.NewGuid(), UserProfileId = owner, DisplayName = "PRIVATE_JOURNAL person" };
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = owner, Title = "PRIVATE_JOURNAL title", Description = "Synthetic evidence", OccurredAt = DateTimeOffset.UtcNow };
        interaction.Participants.Add(new() { InteractionId = interaction.Id, PersonId = person.Id });
        var attachment = new InteractionAttachment { Id = Guid.NewGuid(), InteractionId = interaction.Id, Type = AttachmentType.Audio, Status = AttachmentStatus.Ready, StorageKey = key, MimeType = "audio/wav" };
        var fact = new PersonFact { Id = Guid.NewGuid(), PersonId = person.Id, Value = "Synthetic confirmed fact" };
        database.People.Add(person); database.Interactions.Add(interaction); database.InteractionAttachments.Add(attachment); database.PersonFacts.Add(fact);
        database.AttachmentTranscripts.Add(new() { AttachmentId = attachment.Id, CorrectedText = "Reviewed words", ReviewStatus = TranscriptReviewStatus.Reviewed,
            ReviewedAt = DateTimeOffset.UtcNow, SourceStorageKey = key, SourceSha256 = Convert.ToHexString(SHA256.HashData(audio)), Version = Guid.NewGuid() });
        await database.SaveChangesAsync(Cancellation);
        return new(owner, person.Id, interaction.Id, attachment.Id, fact.Id);
    }

    private TestHost Host(Action<IServiceCollection>? configure = null, string? credentialDirectory = null,
        Dictionary<string, string?>? configuration = null) => new(fixture, configure, credentialDirectory, configuration);
    private static async Task<SocialTelemetry.Api.Features.LocalProfiles.Get.Response> CurrentAsync(HttpClient client) =>
        await client.GetFromJsonAsync<SocialTelemetry.Api.Features.LocalProfiles.Get.Response>("/local-profiles", Cancellation) ?? throw new InvalidOperationException();
    private static async Task<ChatGptConnectionStatus> StatusAsync(HttpClient client) =>
        await client.GetFromJsonAsync<ChatGptConnectionStatus>("/ai-connection/chatgpt/status", Cancellation) ?? throw new InvalidOperationException();
    private static void SetOwner(HttpClient client, Guid owner, Guid version)
    {
        client.DefaultRequestHeaders.Remove(LocalProfileMiddleware.OwnerHeader); client.DefaultRequestHeaders.Remove(LocalProfileMiddleware.VersionHeader);
        client.DefaultRequestHeaders.Add(LocalProfileMiddleware.OwnerHeader, owner.ToString()); client.DefaultRequestHeaders.Add(LocalProfileMiddleware.VersionHeader, version.ToString());
    }
    private static async Task SwitchAsync(HttpClient client, Guid owner)
    {
        var before = await CurrentAsync(client);
        using var response = await client.PutAsJsonAsync("/local-profiles/current", new { UserProfileId = owner, ExpectedVersion = before.Version }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var selected = await response.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.LocalProfiles.Select.Response>(Cancellation) ?? throw new InvalidOperationException();
        SetOwner(client, owner, selected.Version);
    }
    private static async Task AdoptAsync(HttpClient client, Guid connection)
    {
        using var response = await client.PutAsJsonAsync("/ai-connection/chatgpt/connection", new { ConnectionId = connection, AdoptUnassigned = true }, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    private static async Task<Guid> LegacyAsync(TestHost host)
    {
        var store = host.Application.Services.GetRequiredService<ChatGptCredentialStore>();
        using var storageLock = await store.LockAsync(Cancellation);
        var state = await store.ReadAsync(Cancellation);
        var account = new ChatGptAccount { ClientId = "fixture-" + Guid.NewGuid(), SelectedModel = "gpt-5.6-sol", Tokens = new()
        {
            AccessToken = "private-access-synthetic", SessionId = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Scopes = [ChatGptProtocol.DirectScope, "resource.invoke"]
        } };
        state.Accounts.Add(account); state.ActiveConnectionId = account.Id;
        await store.SaveAsync(state, Cancellation);
        return account.Id;
    }
    private static async Task CodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }
    private static string CompletedResult(Guid person)
    {
        var result = new InteractionAnalysisResult("Synthetic complete analysis")
        {
            ObservedFacts = ["Recorded words"], Uncertainties = ["Intent unknown"],
            SuggestedProfileUpdates = [new(person, "Notes", "Needs explicit review")]
        };
        var text = JsonSerializer.Serialize(result, AiContextBuilder.JsonOptions);
        return "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text }) + "\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"model\":\"gpt-5.6-sol\"}}\n\n";
    }

    private sealed record Seed(Guid Owner, Guid Person, Guid Interaction, Guid Audio, Guid Fact);
    private sealed class TestHost : IDisposable
    {
        public TestBrowser Browser { get; } = new();
        public FakeChatGptServer Server { get; }
        public WebApplicationFactory<Program> Application { get; }
        public string CredentialDirectory { get; }
        public TestHost(PeopleApiFixture fixture, Action<IServiceCollection>? configure, string? credentialDirectory,
            Dictionary<string, string?>? configuration)
        {
            CredentialDirectory = credentialDirectory ?? Path.GetFullPath(Path.Combine(fixture.AttachmentStorageDirectory, "..", "credentials-" + Guid.NewGuid()));
            Server = new(Browser) { ModelsBody = """{"models":[{"slug":"gpt-5.6-sol","display_name":"First","visibility":"list"},{"slug":"gpt-5.6","display_name":"Second","visibility":"list"}]}""" };
            Application = fixture.WithServices(services =>
            {
                services.Configure<ChatGptOptions>(options => options.DataDirectory = CredentialDirectory);
                services.RemoveAll<ISystemBrowser>(); services.AddSingleton<ISystemBrowser>(Browser);
                services.AddHttpClient<ChatGptHttpClient>().ConfigurePrimaryHttpMessageHandler(() => Server).RemoveAllLoggers();
                services.AddSingleton<IStartupFilter>(new Loopback()); configure?.Invoke(services);
            }).WithWebHostBuilder(builder =>
            {
                if (configuration is not null)
                    builder.ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(configuration));
            });
        }
        public HttpClient Client()
        {
            var client = Application.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5059"), AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1"); return client;
        }
        public string Callback() => QueryHelpers.AddQueryString(ChatGptOptions.CallbackPath, new Dictionary<string, string?>
        {
            ["state"] = QueryHelpers.ParseQuery(Browser.LastUri?.Query ?? throw new InvalidOperationException())["state"].ToString(),
            ["code"] = "private-synthetic-code", ["client_id"] = "fixture-client"
        });
        public void Dispose() { Application.Dispose(); Server.DisposeKey(); }
    }
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await continuation(); }); next(app);
        };
    }
    private sealed class PausedSpeech : ISpeechToTextClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public async Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
        {
            Token = cancellationToken; Started.TrySetResult(); await Continue.Task;
            return new SpeechToTextResult("Late synthetic words", "fake-stt", "fake-model", "v1");
        }
    }
    private sealed class FailSelectionSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context?.ChangeTracker.Entries<ProfileAiSelection>().Any(entry => entry.State is EntityState.Added or EntityState.Modified) == true)
            {
                Armed = false;
                throw new InvalidOperationException("Synthetic SQL publication failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SelectionReadBarrier : DbCommandInterceptor
    {
        private int reads;
        public AppDbContext? Target { get; set; }
        public int ReadNumber { get; init; }
        public Guid ObservedGeneration { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!ReferenceEquals(eventData.Context, Target) || !command.CommandText.Contains("FROM \"ProfileAiSelections\"", StringComparison.Ordinal) ||
                Interlocked.Increment(ref reads) != ReadNumber) return result;
            try
            {
                Assert.True(await result.ReadAsync(cancellationToken));
                ObservedGeneration = result.GetGuid(result.GetOrdinal("Generation"));
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
                return new PrimedReader(result);
            }
            catch { await result.DisposeAsync(); throw; }
        }
    }

    // Preserve the native provider's typed getters and disposal after the interceptor reads
    // its first row. EF still sees that row exactly once; no production test hook is needed.
    private sealed class PrimedReader(DbDataReader inner) : DbDataReader
    {
        private bool primed = true;
        public override bool Read()
        {
            if (!primed) return inner.Read();
            primed = false;
            return true;
        }
        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            if (!primed) return inner.ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            primed = false;
            return Task.FromResult(true);
        }
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override void Close() => inner.Close();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class AnalysisCommitGate : DbTransactionInterceptor
    {
        public bool Committed { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<InteractionAnalysis>().Any() == true)
            {
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<InteractionAnalysis>().Any() == true) Committed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class SelectionAttempt : DbCommandInterceptor, IDbTransactionInterceptor, IStartupFilter
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                // Model catalog validation needs the protected file before its SQL change.
                // This request therefore waits on the lease, not on a database command.
                if (Armed && context.Request.Method == "PUT" && context.Request.Path == "/ai-connection/chatgpt/model") Entered.TrySetResult();
                await continuation();
            });
            next(app);
        };
        public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Armed && connection is Microsoft.Data.Sqlite.SqliteConnection) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("\"LocalProfileSelection\"", StringComparison.Ordinal)) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CompletedResultGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DelayedCompletedClient(IAiClient inner, CompletedResultGate gate) : IAiClient
    {
        public Task<AiSelectedModel> GetSelectedModelAsync(CancellationToken cancellationToken) => inner.GetSelectedModelAsync(cancellationToken);
        public async Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.GenerateTextAsync(request, cancellationToken);
            gate.Entered.TrySetResult();
            // Deliberately ignore cancellation after a fully completed real adapter response.
            await gate.Continue.Task;
            return result;
        }
        public Task ValidateExecutionAsync(AiTextResponse result, CancellationToken cancellationToken) => inner.ValidateExecutionAsync(result, cancellationToken);
        public Task<IAiExecutionLease> AcquireExecutionLeaseAsync(AiTextResponse result, CancellationToken cancellationToken) => inner.AcquireExecutionLeaseAsync(result, cancellationToken);
    }
}

public sealed class ProfileIsolationTests(PeopleApiFixture fixture) : ProfileIsolationTestsContract<PeopleApiFixture>(fixture);
[Collection("SQLite")]
public sealed class SqliteProfileIsolationTests(SqliteApiFixture fixture) : ProfileIsolationTestsContract<SqliteApiFixture>(fixture);
