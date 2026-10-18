using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Features.Analysis;

public abstract partial class AudioAnalysisTestsContract<TFixture> where TFixture : PeopleApiFixture
{
    [Theory]
    [InlineData("edit")]
    [InlineData("review")]
    [InlineData("delete")]
    public async Task Transcript_writer_committing_after_inference_before_final_guard_rejects_stale_analysis(string change)
    {
        var data = await SeedAsync();
        var provider = new FakeAnalysisClient();
        var final = new FinalTransactionGate();
        using var application = App(provider, configure: services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(final)));
        using var client = Client(application);
        using var previous = await AnalyzeAsync(client, data);
        Assert.Equal(HttpStatusCode.Created, previous.StatusCode);
        var original = Assert.IsType<Response>(await previous.Content.ReadFromJsonAsync<Response>(Cancellation));
        var inferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInference = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        provider.BeforeCompletion = async () =>
        {
            inferred.TrySetResult();
            await releaseInference.Task.WaitAsync(deadline.Token);
        };
        var request = Task.Run(() => AnalyzeAsync(client, data), deadline.Token);
        await inferred.Task.WaitAsync(deadline.Token);
        await using var sourceScope = fixture.CreateAsyncScope();
        var source = sourceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await source.Database.BeginTransactionAsync(deadline.Token);
        await ChangeTrackedTranscriptAsync(source, data, change, deadline.Token);
        await source.SaveChangesAsync(deadline.Token);
        final.Enabled = true;
        releaseInference.TrySetResult();
        try
        {
            await final.Entered.Task.WaitAsync(deadline.Token);
            await transaction.CommitAsync(deadline.Token);
        }
        finally
        {
            releaseInference.TrySetResult();
            final.Continue.TrySetResult();
        }
        using var response = await request.WaitAsync(deadline.Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("StaleContext", await response.Content.ReadAsStringAsync(Cancellation));
        await AssertAnalysisCountAsync(data, 1);
        await using var checkScope = fixture.CreateAsyncScope();
        Assert.True(await checkScope.ServiceProvider.GetRequiredService<AppDbContext>().InteractionAnalyses.AnyAsync(analysis => analysis.Id == original.Id, Cancellation));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("review")]
    [InlineData("delete")]
    [InlineData("source")]
    public async Task Transcript_and_audio_source_writes_cannot_commit_after_final_check_until_analysis_commit(string change)
    {
        var data = await SeedAsync();
        var commit = new AnalysisCommitGate();
        using var application = App(new FakeAnalysisClient(), configure: services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(commit)));
        using var client = Client(application);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var request = Task.Run(() => AnalyzeAsync(client, data), deadline.Token);
        await commit.Entered.Task.WaitAsync(deadline.Token);
        try
        {
            await using var sourceScope = fixture.CreateAsyncScope();
            var configured = sourceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var options = sourceScope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
            if (configured.Database.IsSqlite())
            {
                var connection = new SqliteConnectionStringBuilder(configured.Database.GetConnectionString()) { DefaultTimeout = 1 };
                options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection.ToString()).Options;
            }
            await using var source = new AppDbContext(options);
            await ChangeTrackedTranscriptAsync(source, data, change, deadline.Token);
            if (source.Database.IsSqlite())
            {
                // WAL readers remain usable, but a competing writer must fail Busy before this commit.
                var busy = await Assert.ThrowsAsync<SqliteException>(() => source.SaveChangesAsync(deadline.Token));
                Assert.Equal(5, busy.SqliteErrorCode);
                commit.Continue.TrySetResult();
                using var response = await request.WaitAsync(deadline.Token);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                await source.SaveChangesAsync(deadline.Token);
            }
            else
            {
                await source.Database.OpenConnectionAsync(deadline.Token);
                var sourcePid = ((NpgsqlConnection)source.Database.GetDbConnection()).ProcessID;
                var write = source.SaveChangesAsync(deadline.Token);
                await WaitForGuardAsync(sourcePid, deadline.Token);
                Assert.False(write.IsCompleted);
                commit.Continue.TrySetResult();
                using var response = await request.WaitAsync(deadline.Token);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                await write;
            }
            await AssertAnalysisCountAsync(data, 1);
        }
        finally { commit.Continue.TrySetResult(); }
    }

    private static async Task ChangeTrackedTranscriptAsync(AppDbContext source, Seed data, string change, CancellationToken cancellationToken)
    {
        if (change == "source")
        {
            var attachment = await source.InteractionAttachments.SingleAsync(attachment => attachment.Id == data.AttachmentId, cancellationToken);
            attachment.StorageKey = Guid.NewGuid().ToString("N");
            return;
        }
        var transcript = await source.AttachmentTranscripts.SingleAsync(transcript => transcript.AttachmentId == data.AttachmentId, cancellationToken);
        if (change == "delete") { source.AttachmentTranscripts.Remove(transcript); return; }
        transcript.Version = Guid.NewGuid();
        if (change == "review") transcript.ReviewedAt = DateTimeOffset.UtcNow;
        else
        {
            transcript.CorrectedText = "Concurrent corrected text";
            transcript.ReviewStatus = TranscriptReviewStatus.Unreviewed;
            transcript.ReviewedAt = null;
        }
    }

    private async Task WaitForGuardAsync(int pid, CancellationToken cancellationToken)
    {
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @pid
                AND query LIKE '%AnalysisContextGuards%' AND cardinality(pg_blocking_pids(pid)) > 0)
            """, (NpgsqlConnection)database.Database.GetDbConnection());
        command.Parameters.AddWithValue("pid", pid);
        while (!Equals(await command.ExecuteScalarAsync(cancellationToken), true)) await Task.Yield();
    }

    private sealed class FinalTransactionGate : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.IsolationLevel == IsolationLevel.Unspecified)
            {
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class AnalysisCommitGate : DbTransactionInterceptor
    {
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
    }
}
