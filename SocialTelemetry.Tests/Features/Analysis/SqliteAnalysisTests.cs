using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Features.Analysis.AnalyzeInteraction;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.AI.Models;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Features.Analysis;

[Collection("SQLite")]
public sealed class SqliteAnalysisTests(SqliteApiFixture fixture) : IClassFixture<SqliteApiFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Real_sqlite_analysis_uses_bounded_owned_context_and_persists_atomic_provenance_and_pending_suggestions()
    {
        var data = await SeedAsync();
        var provider = new FakeAnalysisClient();
        using var application = CreateApplication(provider);
        using var client = CreateClient(application);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await AnalyzeAsync(client, data.InteractionId);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<Response>(Cancellation);
            Assert.NotNull(body);
            Assert.Equal("sqlite-test-provider", body.Provider);
            Assert.Equal("executed-model", body.Model);
        }
        var context = Assert.IsType<InteractionAnalysisContext>(provider.Context);
        var person = Assert.Single(context.Participants);
        Assert.Equal(data.PersonId, person.Id);
        Assert.Equal("Confirmed", Assert.Single(person.ConfirmedFacts).Value);
        Assert.Equal(0.1234567890123456789m, Assert.Single(person.AiInferences).Confidence);
        Assert.Equal(5, context.PreviousInteractions.Count);
        Assert.Equal(context.PreviousInteractions.OrderByDescending(previous => previous.OccurredAt), context.PreviousInteractions);
        Assert.Equal("Synthetic question", context.UserQuestion);
        Assert.Single(context.Evidence);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analyses = await database.InteractionAnalyses.Where(analysis => analysis.InteractionId == data.InteractionId).ToListAsync(Cancellation);
        Assert.Equal(2, analyses.Count);
        Assert.All(analyses, analysis =>
        {
            Assert.Equal("sqlite-test-provider", analysis.Provider);
            Assert.Equal("executed-model", analysis.Model);
            Assert.Equal("interaction-analysis-v1", analysis.SchemaVersion);
            Assert.Equal("analyze-interaction-v1", analysis.PromptVersion);
            Assert.Equal(64, analysis.ContextFingerprint?.Length);
            Assert.NotNull(analysis.ResultJson);
        });
        var suggestions = await database.SuggestedProfileUpdates.Where(suggestion => suggestion.PersonId == data.PersonId).ToListAsync(Cancellation);
        Assert.Equal(2, suggestions.Count);
        Assert.All(suggestions, suggestion => Assert.Equal(SuggestionStatus.Pending, suggestion.Status));
        Assert.Null((await database.People.SingleAsync(person => person.Id == data.PersonId, Cancellation)).Notes);
        Assert.Single(await database.PersonFacts.Where(fact => fact.PersonId == data.PersonId).ToListAsync(Cancellation));
    }

    [Theory]
    [InlineData("interaction")]
    [InlineData("fact-add")]
    [InlineData("fact-delete")]
    [InlineData("evidence")]
    public async Task Writes_commit_during_inference_and_stale_analysis_saves_nothing(string changed)
    {
        var data = await SeedAsync();
        var provider = new FakeAnalysisClient { BeforeCompletion = async () =>
        {
            await using var scope = fixture.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ChangeSourceAsync(database, data, changed);
            await database.SaveChangesAsync(Cancellation);
        } };
        using var application = CreateApplication(provider);
        using var client = CreateClient(application);
        using var response = await AnalyzeAsync(client, data.InteractionId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("StaleContext", await response.Content.ReadAsStringAsync(Cancellation));
        await AssertNoAnalysisAsync(data.InteractionId);
    }

    [Theory]
    [InlineData("interaction")]
    [InlineData("fact-add")]
    public async Task Source_writer_committing_after_inference_but_before_final_guard_is_revalidated(string changed)
    {
        var data = await SeedAsync();
        await using var sourceScope = fixture.CreateAsyncScope();
        var source = sourceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInference = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalStarted = new FinalWriteGate();
        var provider = new FakeAnalysisClient { BeforeCompletion = async () =>
        {
            inferred.TrySetResult();
            await releaseInference.Task.WaitAsync(Cancellation);
        } };
        using var application = CreateApplication(provider, finalStarted);
        using var client = CreateClient(application);
        var analysisTask = Task.Run(() => AnalyzeAsync(client, data.InteractionId), Cancellation);
        await inferred.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        await using var transaction = await source.Database.BeginTransactionAsync(Cancellation);
        await ChangeSourceAsync(source, data, changed);
        await source.SaveChangesAsync(Cancellation);
        finalStarted.Enabled = true;
        releaseInference.TrySetResult();
        try
        {
            await finalStarted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            await transaction.CommitAsync(Cancellation);
        }
        finally { finalStarted.Continue.TrySetResult(); }
        using var response = await analysisTask.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("StaleContext", await response.Content.ReadAsStringAsync(Cancellation));
        await AssertNoAnalysisAsync(data.InteractionId);
    }

    [Theory]
    [InlineData("interaction")]
    [InlineData("fact-add")]
    [InlineData("fact-delete")]
    public async Task Source_changes_cannot_commit_after_final_check_until_analysis_commit_releases_writer(string changed)
    {
        var data = await SeedAsync();
        var gate = new CommitGate();
        using var application = CreateApplication(new FakeAnalysisClient(), gate);
        using var client = CreateClient(application);
        var analysisTask = Task.Run(() => AnalyzeAsync(client, data.InteractionId), Cancellation);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        try
        {
            await using var sourceScope = fixture.CreateAsyncScope();
            var configured = sourceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var connection = new SqliteConnectionStringBuilder(configured.Database.GetConnectionString()) { DefaultTimeout = 1 };
            await using var source = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection.ToString()).Options);
            await ChangeSourceAsync(source, data, changed);
            // Reading is still possible in WAL mode; committing a competing write is not.
            var busy = await Assert.ThrowsAsync<SqliteException>(() => source.SaveChangesAsync(Cancellation));
            Assert.Equal(5, busy.SqliteErrorCode);
            gate.Continue.TrySetResult();
            using var response = await analysisTask.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            await source.SaveChangesAsync(Cancellation);
        }
        finally { gate.Continue.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_execution_or_cancelled_final_lease_rolls_back_analysis_and_suggestions(bool cancelled)
    {
        var data = await SeedAsync();
        using var lifetime = new CancellationTokenSource();
        var provider = new FakeAnalysisClient
        {
            Lifetime = lifetime.Token,
            ValidateLease = () =>
            {
                if (cancelled) lifetime.Cancel();
                else throw new AiProviderException(AiFailure.ExecutionInvalidated);
            }
        };
        using var application = CreateApplication(provider);
        using var client = CreateClient(application);
        using var response = await AnalyzeAsync(client, data.InteractionId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNoAnalysisAsync(data.InteractionId);
    }

    [Fact]
    public async Task Busy_final_writer_returns_safe_conflict_and_persists_nothing()
    {
        var data = await SeedAsync();
        var inferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueInference = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeAnalysisClient { BeforeCompletion = async () =>
        {
            inferred.TrySetResult();
            await continueInference.Task.WaitAsync(Cancellation);
        } };
        using var configured = CreateApplication(provider);
        using var application = configured.WithWebHostBuilder(builder => builder.UseSetting("Persistence:SqliteTimeoutSeconds", "1"));
        using var client = CreateClient(application);
        var analysisTask = Task.Run(() => AnalyzeAsync(client, data.InteractionId), Cancellation);
        await inferred.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        await using var scope = fixture.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await writer.Database.BeginTransactionAsync(Cancellation);
        continueInference.TrySetResult();
        using var response = await analysisTask.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.Contains("StaleContext", error);
        Assert.DoesNotContain("SQLite", error);
        await AssertNoAnalysisAsync(data.InteractionId);
    }

    private WebApplicationFactory<Program> CreateApplication(FakeAnalysisClient provider, IInterceptor? interceptor = null) =>
        fixture.WithServices(services =>
        {
            services.RemoveAll<IAiClient>();
            services.AddSingleton<IAiClient>(provider);
            services.AddSingleton<IStartupFilter, LocalAddressFilter>();
            if (interceptor is not null) services.AddDbContext<AppDbContext>(options => options.AddInterceptors(interceptor));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> application)
    {
        var client = application.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5059") });
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1");
        return client;
    }

    private static Task<HttpResponseMessage> AnalyzeAsync(HttpClient client, Guid interactionId) =>
        client.PostAsJsonAsync($"/interactions/{interactionId}/analyze", new { UserQuestion = "Synthetic question" }, Cancellation);

    private async Task AssertNoAnalysisAsync(Guid interactionId)
    {
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.InteractionAnalyses.AnyAsync(analysis => analysis.InteractionId == interactionId, Cancellation));
        Assert.False(await database.SuggestedProfileUpdates.AnyAsync(suggestion => suggestion.InteractionAnalysis.InteractionId == interactionId, Cancellation));
    }

    private static async Task ChangeSourceAsync(AppDbContext database, Seed data, string changed)
    {
        switch (changed)
        {
            case "interaction":
                (await database.Interactions.SingleAsync(interaction => interaction.Id == data.InteractionId, Cancellation)).Description = "Changed";
                break;
            case "fact-add":
                database.PersonFacts.Add(new() { Id = Guid.NewGuid(), PersonId = data.PersonId, Value = "New confirmed fact", CreatedAt = DateTimeOffset.UtcNow });
                break;
            case "fact-delete":
                database.PersonFacts.Remove(await database.PersonFacts.SingleAsync(fact => fact.PersonId == data.PersonId, Cancellation));
                break;
            case "evidence":
                (await database.InteractionAttachments.SingleAsync(attachment => attachment.InteractionId == data.InteractionId, Cancellation)).TextContent = "Changed evidence";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(changed));
        }
    }

    private async Task<Seed> SeedAsync()
    {
        var profileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(new() { UserProfileId = profileId, DisplayName = "Synthetic participant" });
        await fixture.CreatePersonAsync(new() { UserProfileId = await fixture.CreateUserProfileAsync(), DisplayName = "Unrelated private person" });
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var instant = new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
        var current = new Interaction { Id = Guid.NewGuid(), UserProfileId = profileId, Title = "Synthetic current", Description = "Synthetic greeting",
            OccurredAt = instant, CreatedAt = instant, Participants = [new() { PersonId = personId }] };
        database.Interactions.Add(current);
        for (var index = 1; index <= 7; index++)
            database.Interactions.Add(new Interaction { Id = Guid.NewGuid(), UserProfileId = profileId, Title = $"Previous {index}", Description = "Synthetic",
                OccurredAt = instant.AddDays(-index).ToOffset(TimeSpan.FromHours(index)), CreatedAt = instant, Participants = [new() { PersonId = personId }] });
        database.PersonFacts.Add(new() { Id = Guid.NewGuid(), PersonId = personId, Value = "Confirmed", CreatedAt = instant });
        database.PersonInferences.Add(new() { Id = Guid.NewGuid(), PersonId = personId, Value = "Hypothesis", Confidence = 0.1234567890123456789m, CreatedAt = instant, SourceInteractionId = current.Id });
        database.InteractionAttachments.Add(new() { Id = Guid.NewGuid(), InteractionId = current.Id, Type = AttachmentType.Text, Status = AttachmentStatus.Ready, TextContent = "Hello", CreatedAt = instant });
        await database.SaveChangesAsync(Cancellation);
        return new Seed(current.Id, personId);
    }

    private sealed record Seed(Guid InteractionId, Guid PersonId);

    private sealed class FinalWriteGate : DbTransactionInterceptor
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

    private sealed class CommitGate : DbTransactionInterceptor
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

    private sealed class LocalAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, nextMiddleware) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await nextMiddleware(); });
            next(builder);
        };
    }

    private sealed class FakeAnalysisClient : IAiClient
    {
        public InteractionAnalysisContext? Context { get; private set; }
        public Func<Task>? BeforeCompletion { get; init; }
        public Action? ValidateLease { get; init; }
        public CancellationToken Lifetime { get; init; }
        public Task<AiSelectedModel> GetSelectedModelAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AiSelectedModel("sqlite-test-provider", "requested-model", [AiCapability.Text, AiCapability.StructuredOutput]));
        public async Task<AiTextResponse> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken)
        {
            Context = JsonSerializer.Deserialize<InteractionAnalysisContext>(request.Input, AiContextBuilder.JsonOptions)
                ?? throw new InvalidOperationException();
            if (BeforeCompletion is not null) await BeforeCompletion();
            var result = new InteractionAnalysisResult("Synthetic result")
            {
                Interpretations = [new AnalysisInterpretation("Uncertain interpretation", 0.5m, [Context.CurrentInteraction.Id])],
                SuggestedProfileUpdates = [new(Context.Participants[0].Id, "Notes", "A pending suggestion")]
            };
            return new(JsonSerializer.Serialize(result, AiContextBuilder.JsonOptions), "sqlite-test-provider", "requested-model", "executed-model")
                { LifetimeCancellationToken = Lifetime };
        }
        public Task ValidateExecutionAsync(AiTextResponse result, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IAiExecutionLease> AcquireExecutionLeaseAsync(AiTextResponse result, CancellationToken cancellationToken) =>
            Task.FromResult<IAiExecutionLease>(new Lease(ValidateLease));
        private sealed class Lease(Action? validate) : IAiExecutionLease
        {
            public Task ValidateAsync(CancellationToken cancellationToken) { validate?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public void Dispose() { }
        }
    }
}
