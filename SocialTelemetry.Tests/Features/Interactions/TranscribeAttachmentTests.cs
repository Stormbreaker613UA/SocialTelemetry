using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Api.Infrastructure.Transcription;
using SocialTelemetry.Tests.Features.People;
using Transcribe = SocialTelemetry.Api.Features.Interactions.TranscribeAttachment;
using PutTranscript = SocialTelemetry.Api.Features.Interactions.PutTranscript;

namespace SocialTelemetry.Tests.Features.Interactions;

public abstract class TranscribeAttachmentTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly byte[] Audio = [1, 2, 3, 4];

    [Fact]
    public async Task Successful_transcription_persists_generated_text_provenance_and_requires_review()
    {
        var data = await SeedAsync();
        var speech = new FakeSpeech();
        using var application = App(speech);
        using var client = Client(application);
        using var response = await client.PostAsync(Route(data), null, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await ReadAsync(response);
        Assert.Equal("Synthetic transcript", result.GeneratedText);
        Assert.Null(result.CorrectedText);
        Assert.Equal(TranscriptReviewStatus.Unreviewed, result.ReviewStatus);
        Assert.Null(result.ReviewedAt);
        Assert.False(result.Reused);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Audio)), result.SourceSha256);
        Assert.Equal("fake-stt", result.Provider);
        Assert.Equal("multilingual-test", result.Model);
        Assert.Equal("fake-v1", result.TranscriptionVersion);
        Assert.Equal(Audio, speech.Input);
        var stored = await FindAsync(data);
        Assert.NotNull(stored);
        Assert.Equal(result.Version, stored.Version);
        Assert.Equal(result.GeneratedText, stored.GeneratedText);
        Assert.Equal(data.Key, stored.SourceStorageKey);
        using var reviewed = await fixture.Client.PutAsJsonAsync(TranscriptRoute(data),
            new { ExpectedVersion = result.Version, ConfirmReviewed = true }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generated_transcript_is_reused_without_changing_version_or_review(bool reviewed)
    {
        var data = await SeedAsync();
        var speech = new FakeSpeech();
        using var application = App(speech);
        using var client = Client(application);
        using var first = await client.PostAsync(Route(data), null, Cancellation);
        var initial = await ReadAsync(first);
        if (reviewed)
        {
            using var review = await fixture.Client.PutAsJsonAsync(TranscriptRoute(data),
                new { ExpectedVersion = initial.Version, ConfirmReviewed = true }, Cancellation);
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }
        var before = await FindAsync(data);
        using var reused = await client.PostAsync(Route(data), null, Cancellation);
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        var result = await ReadAsync(reused);
        Assert.True(result.Reused);
        Assert.Equal(before!.Version, result.Version);
        Assert.Equal(before.ReviewStatus, result.ReviewStatus);
        Assert.Equal(1, speech.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_transcript_is_preserved_even_without_configured_native_dependencies(bool reviewed)
    {
        var data = await SeedAsync();
        using var manual = await fixture.Client.PutAsJsonAsync(TranscriptRoute(data), new { CorrectedText = "Manual correction" }, Cancellation);
        var original = Assert.IsType<PutTranscript.Response>(await manual.Content.ReadFromJsonAsync<PutTranscript.Response>(Cancellation));
        if (reviewed)
        {
            using var review = await fixture.Client.PutAsJsonAsync(TranscriptRoute(data),
                new { ExpectedVersion = original.Version, ConfirmReviewed = true }, Cancellation);
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }
        // Actual adapter is intentionally unconfigured; reuse must not call it.
        using var application = App(null);
        using var client = Client(application);
        var before = await FindAsync(data);
        using var response = await client.PostAsync(Route(data), null, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadAsync(response);
        Assert.True(result.Reused);
        Assert.Equal("Manual correction", result.CorrectedText);
        Assert.Null(result.GeneratedText);
        Assert.Equal(before!.Version, result.Version);
        Assert.Equal(before.ReviewStatus, result.ReviewStatus);
    }

    [Theory]
    [InlineData(SpeechToTextFailure.ConfigurationRequired, HttpStatusCode.ServiceUnavailable)]
    [InlineData(SpeechToTextFailure.InvalidAudio, HttpStatusCode.BadRequest)]
    [InlineData(SpeechToTextFailure.DecoderFailed, HttpStatusCode.BadGateway)]
    [InlineData(SpeechToTextFailure.TranscriptionFailed, HttpStatusCode.BadGateway)]
    [InlineData(SpeechToTextFailure.TimedOut, HttpStatusCode.GatewayTimeout)]
    public async Task Safe_provider_failures_leave_no_transcript(SpeechToTextFailure failure, HttpStatusCode status)
    {
        var data = await SeedAsync();
        using var application = App(new FakeSpeech { Execute = _ => throw new SpeechToTextException(failure) });
        using var client = Client(application);
        using var response = await client.PostAsync(Route(data), null, Cancellation);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.Contains(failure.ToString(), body);
        Assert.DoesNotContain(data.Key, body);
        Assert.Null(await FindAsync(data));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("provenance")]
    public async Task Invalid_provider_result_cannot_be_persisted(string fault)
    {
        var data = await SeedAsync();
        var output = fault == "text" ? new SpeechToTextResult(new string('x', 12001), "provider", "model", "v1")
            : new SpeechToTextResult("Text", "", "model", "v1");
        using var application = App(new FakeSpeech { Execute = _ => Task.FromResult(output) });
        using var client = Client(application);
        using var response = await client.PostAsync(Route(data), null, Cancellation);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null(await FindAsync(data));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("source")]
    [InlineData("status")]
    [InlineData("edit")]
    public async Task Changed_source_or_newer_user_edit_during_execution_prevents_persistence(string change)
    {
        var data = await SeedAsync();
        if (change == "edit")
        {
            await using var scope = fixture.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.AttachmentTranscripts.Add(new AttachmentTranscript { AttachmentId = data.AttachmentId,
                Version = Guid.NewGuid(), SourceStorageKey = data.Key, SourceSha256 = Convert.ToHexString(SHA256.HashData(Audio)),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync(Cancellation);
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var application = App(new FakeSpeech { Execute = async token =>
        { entered.TrySetResult(); await gate.Task.WaitAsync(token); return Output; } });
        using var client = Client(application);
        var request = client.PostAsync(Route(data), null, Cancellation);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            switch (change)
            {
                case "delete":
                    using (var delete = await fixture.Client.DeleteAsync($"/interactions/{data.InteractionId}/attachments/{data.AttachmentId}", Cancellation))
                        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
                    break;
                case "source":
                    await File.WriteAllBytesAsync(Path.Combine(fixture.AttachmentStorageDirectory, data.Key), [9], Cancellation);
                    break;
                case "status":
                    await using (var scope = fixture.CreateAsyncScope())
                    {
                        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        (await database.InteractionAttachments.SingleAsync(attachment => attachment.Id == data.AttachmentId, Cancellation)).Status = AttachmentStatus.Deleting;
                        await database.SaveChangesAsync(Cancellation);
                    }
                    break;
                case "edit":
                    var old = await FindAsync(data);
                    using (var edit = await fixture.Client.PutAsJsonAsync(TranscriptRoute(data),
                        new { ExpectedVersion = old!.Version, CorrectedText = "User wins" }, Cancellation))
                        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
                    break;
            }
            gate.TrySetResult();
            using var response = await request;
            Assert.Equal(change == "delete" ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, response.StatusCode);
            var stored = await FindAsync(data);
            if (change == "edit") { Assert.NotNull(stored); Assert.Equal("User wins", stored.CorrectedText); Assert.Null(stored.GeneratedText); }
            else Assert.Null(stored);
        }
        finally { gate.TrySetResult(); }
    }

    [Fact]
    public async Task Two_simultaneous_transcriptions_cannot_overwrite_one_another()
    {
        var data = await SeedAsync();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var application = App(new FakeSpeech { Execute = async token =>
        {
            if (Interlocked.Increment(ref executions) == 2) bothEntered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return Output;
        } });
        using var client = Client(application);
        var first = client.PostAsync(Route(data), null, Cancellation);
        var second = client.PostAsync(Route(data), null, Cancellation);
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            finish.TrySetResult();
            var results = await Task.WhenAll(first, second);
            try
            {
                Assert.Single(results, response => response.StatusCode == HttpStatusCode.Created);
                Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
                var winner = await ReadAsync(results.Single(response => response.IsSuccessStatusCode));
                Assert.Equal(winner.Version, (await FindAsync(data))!.Version);
            }
            finally { foreach (var result in results) result.Dispose(); }
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task Caller_cancellation_does_not_create_a_transcript()
    {
        var data = await SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var application = App(new FakeSpeech { Execute = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return Output; }
            finally { stopped.TrySetResult(); }
        } });
        using var client = Client(application);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var request = client.PostAsync(Route(data), null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await request; });
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        Assert.Null(await FindAsync(data));
    }

    [Theory]
    [InlineData("foreign", HttpStatusCode.NotFound)]
    [InlineData("type", HttpStatusCode.BadRequest)]
    [InlineData("pending", HttpStatusCode.Conflict)]
    [InlineData("missing-file", HttpStatusCode.NotFound)]
    [InlineData("digest", HttpStatusCode.Conflict)]
    public async Task Invalid_source_is_rejected_before_stt(string fault, HttpStatusCode expected)
    {
        var data = await SeedAsync();
        var speech = new FakeSpeech();
        using var application = App(speech);
        using var client = Client(application);
        if (fault == "digest")
        {
            using var manual = await fixture.Client.PutAsJsonAsync(TranscriptRoute(data), new { CorrectedText = "Manual" }, Cancellation);
            Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
            await File.WriteAllBytesAsync(Path.Combine(fixture.AttachmentStorageDirectory, data.Key), [9], Cancellation);
        }
        if (fault is "type" or "pending")
        {
            await using var scope = fixture.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var attachment = await database.InteractionAttachments.SingleAsync(attachment => attachment.Id == data.AttachmentId, Cancellation);
            if (fault == "type") attachment.Type = AttachmentType.Image;
            else attachment.Status = AttachmentStatus.Pending;
            await database.SaveChangesAsync(Cancellation);
        }
        if (fault == "missing-file") File.Delete(Path.Combine(fixture.AttachmentStorageDirectory, data.Key));
        var route = fault == "foreign" ? Route(data with { InteractionId = Guid.NewGuid() }) : Route(data);
        using var response = await client.PostAsync(route, null, Cancellation);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, speech.Calls);
    }

    [Fact]
    public async Task Native_transcription_requires_local_request_protection()
    {
        var data = await SeedAsync();
        using var application = App(null);
        using var client = application.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5059") });
        using var response = await client.PostAsync(Route(data), null, Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App(FakeSpeech? speech) =>
        fixture.WithServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new LocalAddressFilter());
            if (speech is null) return;
            services.RemoveAll<ISpeechToTextClient>(); services.AddSingleton<ISpeechToTextClient>(speech);
        });
    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> application)
    {
        var client = application.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5059") });
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1");
        return client;
    }
    private async Task<SeedData> SeedAsync()
    {
        var profile = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        using var input = new MemoryStream(Audio);
        var key = await storage.SaveAsync(input, "synthetic.audio", "audio/unknown", Cancellation);
        await storage.CompleteUploadAsync(key, Cancellation);
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = profile, Title = "Synthetic", Description = "Test",
            OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow };
        var attachment = new InteractionAttachment { Id = Guid.NewGuid(), InteractionId = interaction.Id, Type = AttachmentType.Audio,
            Status = AttachmentStatus.Ready, StorageKey = key, CreatedAt = DateTimeOffset.UtcNow };
        database.Interactions.Add(interaction); database.InteractionAttachments.Add(attachment);
        await database.SaveChangesAsync(Cancellation);
        return new(interaction.Id, attachment.Id, key);
    }
    private async Task<AttachmentTranscript?> FindAsync(SeedData data)
    {
        await using var scope = fixture.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AttachmentTranscripts.AsNoTracking()
            .SingleOrDefaultAsync(transcript => transcript.AttachmentId == data.AttachmentId, Cancellation);
    }
    private static Task<Transcribe.Response?> DeserializeAsync(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<Transcribe.Response>(Cancellation);
    private static async Task<Transcribe.Response> ReadAsync(HttpResponseMessage response) => Assert.IsType<Transcribe.Response>(await DeserializeAsync(response));
    private static string Route(SeedData data) => $"/interactions/{data.InteractionId}/attachments/{data.AttachmentId}/transcribe";
    private static string TranscriptRoute(SeedData data) => $"/interactions/{data.InteractionId}/attachments/{data.AttachmentId}/transcript";
    private static SpeechToTextResult Output => new("Synthetic transcript", "fake-stt", "multilingual-test", "fake-v1");
    private sealed record SeedData(Guid InteractionId, Guid AttachmentId, string Key);
    private sealed class LocalAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await continuation(); });
            next(builder);
        };
    }
    private sealed class FakeSpeech : ISpeechToTextClient
    {
        public int Calls;
        public byte[]? Input { get; private set; }
        public Func<CancellationToken, Task<SpeechToTextResult>>? Execute { get; init; }
        public async Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            using var copy = new MemoryStream(); await audio.CopyToAsync(copy, cancellationToken); Input = copy.ToArray();
            return Execute is null ? Output : await Execute(cancellationToken);
        }
    }
}

public sealed class TranscribeAttachmentTests(PeopleApiFixture fixture) : TranscribeAttachmentTestsContract<PeopleApiFixture>(fixture);
[Collection("SQLite")]
public sealed class SqliteTranscribeAttachmentTests(SqliteApiFixture fixture) : TranscribeAttachmentTestsContract<SqliteApiFixture>(fixture);
