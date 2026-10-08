using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Tests.Features.People;
using PutTranscript = SocialTelemetry.Api.Features.Interactions.PutTranscript;

namespace SocialTelemetry.Tests.Features.Interactions;

public abstract class AttachmentTranscriptTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly byte[] AudioBytes = [10, 20, 30, 40];

    [Fact]
    public async Task Manual_transcript_is_persisted_unreviewed_and_can_be_read()
    {
        var data = await SeedAsync();
        using var response = await PutAsync(data, new { CorrectedText = "Manual transcript" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync(response);
        Assert.Null(created.GeneratedText);
        Assert.Equal("Manual transcript", created.CorrectedText);
        Assert.Equal(created.CorrectedText, created.EffectiveText);
        Assert.Equal(TranscriptReviewStatus.Unreviewed, created.ReviewStatus);
        Assert.Null(created.ReviewedAt);
        Assert.Null(created.Provider);
        Assert.Null(created.Model);
        Assert.NotEqual(Guid.Empty, created.Version);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(AudioBytes)), created.SourceSha256);

        using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertPersistedResponse(created, await ReadAsync(get));
        await using var scope = fixture.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AttachmentTranscripts
            .AsNoTracking().SingleAsync(transcript => transcript.AttachmentId == data.AttachmentId, Cancellation);
        Assert.Equal(data.StorageKey, stored.SourceStorageKey);
        Assert.Equal(created.Version, stored.Version);
    }

    [Fact]
    public async Task Review_is_explicit_and_editing_invalidates_previous_confirmation()
    {
        var data = await SeedAsync();
        var created = await CreateAsync(data);
        using var review = await PutAsync(data, new { ExpectedVersion = created.Version, ConfirmReviewed = true });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var reviewed = await ReadAsync(review);
        Assert.Equal(TranscriptReviewStatus.Reviewed, reviewed.ReviewStatus);
        Assert.NotNull(reviewed.ReviewedAt);
        Assert.NotEqual(created.Version, reviewed.Version);

        using var edit = await PutAsync(data, new { ExpectedVersion = reviewed.Version, CorrectedText = "Corrected name" });
        var edited = await ReadAsync(edit);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(TranscriptReviewStatus.Unreviewed, edited.ReviewStatus);
        Assert.Null(edited.ReviewedAt);
        Assert.NotEqual(reviewed.Version, edited.Version);
        AssertTimestamp(created.CreatedAt, edited.CreatedAt);
        using var reReview = await PutAsync(data, new { ExpectedVersion = edited.Version, ConfirmReviewed = true });
        Assert.Equal(TranscriptReviewStatus.Reviewed, (await ReadAsync(reReview)).ReviewStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_text_cannot_be_confirmed_in_the_same_request(bool existing)
    {
        var data = await SeedAsync();
        var created = existing ? await CreateAsync(data) : null;
        using var response = await PutAsync(data, new { ExpectedVersion = created?.Version,
            CorrectedText = "New text", ConfirmReviewed = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
        Assert.Equal(existing ? HttpStatusCode.OK : HttpStatusCode.NotFound, get.StatusCode);
        if (created is not null) AssertPersistedResponse(created, await ReadAsync(get));
    }

    [Fact]
    public async Task User_edit_and_review_preserve_generated_text_and_transcription_provenance()
    {
        var data = await SeedAsync();
        var version = Guid.NewGuid();
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.AttachmentTranscripts.Add(NewTranscript(data, version));
            await database.SaveChangesAsync(Cancellation);
        }
        using var review = await PutAsync(data, new { ExpectedVersion = version, ConfirmReviewed = true });
        var reviewed = await ReadAsync(review);
        Assert.Equal("Generated transcript", reviewed.EffectiveText);
        using var edit = await PutAsync(data, new { ExpectedVersion = reviewed.Version, CorrectedText = "User correction",
            GeneratedText = "Forged generated text", Provider = "forged" });
        var corrected = await ReadAsync(edit);
        Assert.Equal("Generated transcript", corrected.GeneratedText);
        Assert.Equal("User correction", corrected.EffectiveText);
        Assert.Equal("synthetic-stt", corrected.Provider);
        Assert.Equal("synthetic-model", corrected.Model);
        Assert.Equal("synthetic-v1", corrected.TranscriptionVersion);
        Assert.Equal(TranscriptReviewStatus.Unreviewed, corrected.ReviewStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_version_cannot_edit_or_review(bool review)
    {
        var data = await SeedAsync();
        var original = await CreateAsync(data);
        using var edit = await PutAsync(data, new { ExpectedVersion = original.Version, CorrectedText = "Winning edit" });
        var winner = await ReadAsync(edit);
        using var stale = await PutAsync(data, new { ExpectedVersion = original.Version,
            CorrectedText = review ? null : "Losing edit", ConfirmReviewed = review });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
        AssertPersistedResponse(winner, await ReadAsync(get));
    }

    [Fact]
    public async Task Missing_expected_version_cannot_overwrite_existing_transcript()
    {
        var data = await SeedAsync();
        var original = await CreateAsync(data);
        using var response = await PutAsync(data, new { CorrectedText = "Blind overwrite" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
        AssertPersistedResponse(original, await ReadAsync(get));
    }

    [Fact]
    public async Task Expected_version_cannot_create_missing_transcript()
    {
        var data = await SeedAsync();
        using var response = await PutAsync(data, new { ExpectedVersion = Guid.NewGuid(), CorrectedText = "Stale creation" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_creations_or_edits_have_one_winner(bool editing)
    {
        var data = await SeedAsync();
        var original = editing ? await CreateAsync(data) : null;
        var responses = await Task.WhenAll(
            PutAsync(data, new { ExpectedVersion = original?.Version, CorrectedText = "First" }),
            PutAsync(data, new { ExpectedVersion = original?.Version, CorrectedText = "Second" }));
        try
        {
            Assert.Single(responses, response => response.StatusCode == (editing ? HttpStatusCode.OK : HttpStatusCode.Created));
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            var winner = await ReadAsync(responses.Single(response => response.IsSuccessStatusCode));
            using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
            AssertPersistedResponse(winner, await ReadAsync(get));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData(AttachmentType.Text)]
    [InlineData(AttachmentType.Image)]
    [InlineData(AttachmentType.Screenshot)]
    public async Task Transcript_operations_reject_non_audio(AttachmentType type)
    {
        var data = await SeedAsync(type);
        using var put = await PutAsync(data, new { CorrectedText = "Text" });
        using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
    }

    [Theory]
    [InlineData(AttachmentStatus.Pending)]
    [InlineData(AttachmentStatus.Deleting)]
    public async Task Transcript_operations_reject_non_ready_audio(AttachmentStatus status)
    {
        var data = await SeedAsync(status: status);
        using var put = await PutAsync(data, new { CorrectedText = "Text" });
        using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, get.StatusCode);
    }

    [Fact]
    public async Task Transcript_is_not_accessible_through_another_profiles_interaction()
    {
        var data = await SeedAsync();
        var other = await SeedAsync();
        var original = await CreateAsync(data);
        var foreignRoute = data with { InteractionId = other.InteractionId };
        using var put = await PutAsync(foreignRoute, new { ExpectedVersion = original.Version, CorrectedText = "Foreign edit" });
        using var get = await fixture.Client.GetAsync(Route(foreignRoute), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var owned = await fixture.Client.GetAsync(Route(data), Cancellation);
        AssertPersistedResponse(original, await ReadAsync(owned));
    }

    [Fact]
    public async Task Route_ids_cannot_be_overridden_by_body_ids()
    {
        var data = await SeedAsync();
        var other = await SeedAsync();
        using var response = await PutAsync(data, new { other.InteractionId, other.AttachmentId, CorrectedText = "Owned text" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(data.AttachmentId, (await ReadAsync(response)).AttachmentId);
        using var get = await fixture.Client.GetAsync(Route(other), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Missing_attachment_or_transcript_returns_not_found()
    {
        var data = await SeedAsync();
        using var missingTranscript = await fixture.Client.GetAsync(Route(data), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, missingTranscript.StatusCode);
        var missing = data with { AttachmentId = Guid.NewGuid() };
        using var put = await PutAsync(missing, new { CorrectedText = "Text" });
        using var get = await fixture.Client.GetAsync(Route(missing), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-long")]
    [InlineData(null)]
    public async Task Manual_transcript_text_is_required_and_bounded(string? input)
    {
        var data = await SeedAsync();
        using var application = fixture.WithServices(services =>
            services.PostConfigure<TranscriptOptions>(options => options.TextCharacters = 5));
        using var client = application.CreateClient();
        using var response = await client.PutAsJsonAsync(Route(data), new { CorrectedText = input }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Configured_text_limit_is_inclusive_and_checks_untrimmed_input()
    {
        var data = await SeedAsync();
        using var application = fixture.WithServices(services =>
            services.PostConfigure<TranscriptOptions>(options => options.TextCharacters = 5));
        using var client = application.CreateClient();
        using var create = await client.PutAsJsonAsync(Route(data), new { CorrectedText = "12345" }, Cancellation);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var original = await ReadAsync(create);
        using var oversized = await client.PutAsJsonAsync(Route(data), new { ExpectedVersion = original.Version,
            CorrectedText = " 12345 " }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task Missing_audio_content_cannot_create_transcript()
    {
        var data = await SeedAsync();
        await using (var scope = fixture.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IAttachmentStorage>().DeleteAsync(data.StorageKey, Cancellation);
        using var response = await PutAsync(data, new { CorrectedText = "Text" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Replacement_audio_cannot_silently_reuse_or_review_old_transcript()
    {
        var data = await SeedAsync();
        var original = await CreateAsync(data);
        // Synthetic external replacement; normal attachment APIs never replace a key's content.
        await File.WriteAllBytesAsync(Path.Combine(fixture.AttachmentStorageDirectory, data.StorageKey), [99], Cancellation);
        using var response = await PutAsync(data, new { ExpectedVersion = original.Version, ConfirmReviewed = true });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var get = await fixture.Client.GetAsync(Route(data), Cancellation);
        AssertPersistedResponse(original, await ReadAsync(get));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attachment_or_interaction_deletion_cascades_transcript_without_affecting_others(bool interaction)
    {
        var data = await SeedAsync();
        var other = await SeedAsync();
        await CreateAsync(data);
        var otherTranscript = await CreateAsync(other);
        var route = interaction ? $"/interactions/{data.InteractionId}" : $"/interactions/{data.InteractionId}/attachments/{data.AttachmentId}";
        using var deleted = await fixture.Client.DeleteAsync(route, Cancellation);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.AttachmentTranscripts.AnyAsync(transcript => transcript.AttachmentId == data.AttachmentId, Cancellation));
        using var get = await fixture.Client.GetAsync(Route(other), Cancellation);
        AssertPersistedResponse(otherTranscript, await ReadAsync(get));
    }

    [Fact]
    public async Task Ef_concurrency_token_prevents_stale_direct_update()
    {
        var data = await SeedAsync();
        await CreateAsync(data);
        await using var staleScope = fixture.CreateAsyncScope();
        var staleDatabase = staleScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stale = await staleDatabase.AttachmentTranscripts.SingleAsync(transcript => transcript.AttachmentId == data.AttachmentId, Cancellation);
        using var updated = await PutAsync(data, new { ExpectedVersion = stale.Version, CorrectedText = "API winner" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        stale.CorrectedText = "Stale direct update";
        stale.Version = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDatabase.SaveChangesAsync(Cancellation));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("edit")]
    [InlineData("delete")]
    public async Task Transcript_writes_participate_in_profile_context_protection(string operation)
    {
        var data = await SeedAsync();
        if (operation != "add") await CreateAsync(data);
        await using var holderScope = fixture.CreateAsyncScope();
        var holder = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var held = await holder.Database.BeginTransactionAsync(Cancellation);
        await holder.LockAnalysisContextAsync(data.ProfileId, Cancellation);
        await using var sourceScope = fixture.CreateAsyncScope();
        var configured = sourceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        if (configured.Database.IsSqlite())
            builder.UseSqlite(new SqliteConnectionStringBuilder(configured.Database.GetConnectionString()) { DefaultTimeout = 1 }.ToString());
        var sourceOptions = configured.Database.IsSqlite() ? builder.Options
            : sourceScope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
        await using var source = new AppDbContext(sourceOptions);
        if (operation == "add") source.AttachmentTranscripts.Add(NewTranscript(data, Guid.NewGuid()));
        else
        {
            var transcript = await source.AttachmentTranscripts.SingleAsync(transcript => transcript.AttachmentId == data.AttachmentId, Cancellation);
            if (operation == "delete") source.AttachmentTranscripts.Remove(transcript);
            else { transcript.CorrectedText = "Changed"; transcript.Version = Guid.NewGuid(); }
        }
        if (source.Database.IsSqlite())
        {
            var busy = await Assert.ThrowsAsync<SqliteException>(() => source.SaveChangesAsync(Cancellation));
            Assert.Equal(5, busy.SqliteErrorCode);
            await held.CommitAsync(Cancellation);
            await source.SaveChangesAsync(Cancellation);
        }
        else
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await source.Database.OpenConnectionAsync(deadline.Token);
            var pid = ((NpgsqlConnection)source.Database.GetDbConnection()).ProcessID;
            var write = source.SaveChangesAsync(deadline.Token);
            try
            {
                await WaitForGuardAsync(pid, deadline.Token);
                Assert.False(write.IsCompleted);
            }
            finally { await held.CommitAsync(Cancellation); }
            await write;
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

    private async Task<SeedData> SeedAsync(AttachmentType type = AttachmentType.Audio, AttachmentStatus status = AttachmentStatus.Ready)
    {
        var profileId = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        using var content = new MemoryStream(AudioBytes);
        var key = await storage.SaveAsync(content, "synthetic.wav", "audio/wav", Cancellation);
        await storage.CompleteUploadAsync(key, Cancellation);
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = profileId,
            Title = "Synthetic audio", Description = "Synthetic test", OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow };
        var attachment = new InteractionAttachment { Id = Guid.NewGuid(), InteractionId = interaction.Id,
            Type = type, Status = status, StorageKey = key, MimeType = "audio/wav", CreatedAt = DateTimeOffset.UtcNow };
        database.Interactions.Add(interaction);
        database.InteractionAttachments.Add(attachment);
        await database.SaveChangesAsync(Cancellation);
        return new SeedData(profileId, interaction.Id, attachment.Id, key);
    }

    private static AttachmentTranscript NewTranscript(SeedData data, Guid version) => new()
    {
        AttachmentId = data.AttachmentId, Version = version, GeneratedText = "Generated transcript",
        SourceStorageKey = data.StorageKey, SourceSha256 = Convert.ToHexString(SHA256.HashData(AudioBytes)),
        Provider = "synthetic-stt", Model = "synthetic-model", TranscriptionVersion = "synthetic-v1",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private Task<HttpResponseMessage> PutAsync(SeedData data, object body) => fixture.Client.PutAsJsonAsync(Route(data), body, Cancellation);
    private static void AssertPersistedResponse(PutTranscript.Response expected, PutTranscript.Response actual)
    {
        // PostgreSQL stores microseconds; SQLite retains ticks. Check only that legitimate rounding.
        AssertTimestamp(expected.CreatedAt, actual.CreatedAt);
        AssertTimestamp(expected.UpdatedAt, actual.UpdatedAt);
        if (expected.ReviewedAt is { } reviewedAt)
        {
            Assert.NotNull(actual.ReviewedAt);
            AssertTimestamp(reviewedAt, actual.ReviewedAt.Value);
        }
        else Assert.Null(actual.ReviewedAt);
        Assert.Equal(expected with { CreatedAt = actual.CreatedAt, UpdatedAt = actual.UpdatedAt, ReviewedAt = actual.ReviewedAt }, actual);
    }
    private static void AssertTimestamp(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.InRange(expected.UtcTicks - actual.UtcTicks, 0, 9);
    private static string Route(SeedData data) => $"/interactions/{data.InteractionId}/attachments/{data.AttachmentId}/transcript";
    private static async Task<PutTranscript.Response> ReadAsync(HttpResponseMessage response) =>
        Assert.IsType<PutTranscript.Response>(await response.Content.ReadFromJsonAsync<PutTranscript.Response>(Cancellation));
    private async Task<PutTranscript.Response> CreateAsync(SeedData data)
    {
        using var response = await PutAsync(data, new { CorrectedText = "Manual text" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }
    private sealed record SeedData(Guid ProfileId, Guid InteractionId, Guid AttachmentId, string StorageKey);
}

public sealed class AttachmentTranscriptTests(PeopleApiFixture fixture) : AttachmentTranscriptTestsContract<PeopleApiFixture>(fixture);

[Collection("SQLite")]
public sealed class SqliteAttachmentTranscriptTests(SqliteApiFixture fixture) : AttachmentTranscriptTestsContract<SqliteApiFixture>(fixture);
