using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.Interactions;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Tests.Features.People;
using AppInteractions = SocialTelemetry.Application.Features.Interactions;

namespace SocialTelemetry.Tests.Features.Interactions;

public abstract class InteractionApplicationTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Direct_create_keeps_validation_ownership_distinct_participants_and_UTC()
    {
        var foreignProfile = await fixture.CreateUserProfileAsync();
        var foreignPerson = await fixture.CreatePersonAsync(new() { UserProfileId = foreignProfile, DisplayName = "Foreign" });
        var owner = await fixture.CreateUserProfileAsync();
        var person = await fixture.CreatePersonAsync(new() { UserProfileId = owner, DisplayName = "Owned" });
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var scenario = scope.ServiceProvider.GetRequiredService<AppInteractions.Create.Scenario>();
        var invalid = await Assert.ThrowsAsync<FeatureValidationException>(() => scenario.ExecuteAsync(
            new() { UserProfileId = foreignProfile, Title = " ", Description = "Valid",
                OccurredAt = DateTimeOffset.UtcNow, ParticipantIds = [foreignPerson] }, Cancellation));
        Assert.Equal("UserProfileId, Title, Description, OccurredAt, and at least one participant are required.", invalid.Message);
        await Assert.ThrowsAsync<NotFoundException>(() => scenario.ExecuteAsync(
            new() { UserProfileId = owner, Title = "Valid", Description = "Valid",
                OccurredAt = DateTimeOffset.UtcNow, ParticipantIds = [foreignPerson] }, Cancellation));
        var occurred = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.FromHours(3));
        var created = await scenario.ExecuteAsync(new() { UserProfileId = owner, Title = "  Title  ",
            Description = "  Description  ", OccurredAt = occurred, ParticipantIds = [person, person] }, Cancellation);
        Assert.Equal(owner, created.UserProfileId);
        Assert.Equal("Title", created.Title);
        Assert.Equal("Description", created.Description);
        Assert.Equal(occurred.ToUniversalTime(), created.OccurredAt);
        Assert.Equal(TimeSpan.Zero, created.OccurredAt.Offset);
        Assert.Equal(person, Assert.Single(created.Participants).PersonId);
        await using var verification = fixture.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await database.InteractionParticipants.CountAsync(participant => participant.InteractionId == created.Id, Cancellation));
    }

    [Fact]
    public async Task Direct_upload_defers_open_and_bounds_actual_bytes_before_any_staged_or_DB_write()
    {
        var data = await SeedAsync();
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        var scenario = new AppInteractions.AddAttachment.Scenario(
            scope.ServiceProvider.GetRequiredService<IAttachmentPersistence>(),
            scope.ServiceProvider.GetRequiredService<LocalProfileOperation>(), storage,
            new UploadOptions { AttachmentMaxBytes = 4 });
        var opens = 0;
        var file = new AppInteractions.AddAttachment.UploadFile(1, "synthetic.bin", "audio/test",
            () => { opens++; return new MemoryStream([1, 2, 3, 4, 5]); });
        await Assert.ThrowsAsync<FeatureValidationException>(() => scenario.ExecuteAsync(
            new(Guid.NewGuid(), (AttachmentType)999, null, file), Cancellation));
        await Assert.ThrowsAsync<NotFoundException>(() => scenario.ExecuteAsync(
            new(Guid.NewGuid(), AttachmentType.Audio, null, file), Cancellation));
        Assert.Equal(0, opens);
        var files = storage.EnumerateFiles().Select(entry => entry.StorageKey).Order().ToArray();
        var staged = storage.EnumerateStagedFiles().Select(entry => entry.StorageKey).Order().ToArray();
        var invalid = await Assert.ThrowsAsync<FeatureValidationException>(() => scenario.ExecuteAsync(
            new(data.InteractionId, AttachmentType.Audio, null, file), Cancellation));
        Assert.Equal("Attachment files exceed the configured upload limit.", invalid.Message);
        Assert.Equal(1, opens);
        Assert.Equal(files, storage.EnumerateFiles().Select(entry => entry.StorageKey).Order().ToArray());
        Assert.Equal(staged, storage.EnumerateStagedFiles().Select(entry => entry.StorageKey).Order().ToArray());
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await database.InteractionAttachments.CountAsync(attachment => attachment.InteractionId == data.InteractionId, Cancellation));
    }

    [Fact]
    public async Task Direct_transcript_failure_after_SQL_save_rolls_back_entire_guarded_mutation()
    {
        var data = await SeedAsync();
        Guid originalVersion;
        await using (var scope = fixture.CreateAsyncScope())
        {
            await BindAsync(scope.ServiceProvider);
            var original = await scope.ServiceProvider.GetRequiredService<AppInteractions.PutTranscript.Scenario>()
                .ExecuteAsync(new() { InteractionId = data.InteractionId, AttachmentId = data.AttachmentId, CorrectedText = "Original" }, Cancellation);
            originalVersion = original.Transcript.Version;
        }
        var fault = new FailTranscriptSave();
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(fault)));
        await using (var scope = application.Services.CreateAsyncScope())
        {
            await BindAsync(scope.ServiceProvider);
            await Assert.ThrowsAsync<SyntheticSaveException>(() =>
                scope.ServiceProvider.GetRequiredService<AppInteractions.PutTranscript.Scenario>()
                    .ExecuteAsync(new() { InteractionId = data.InteractionId, AttachmentId = data.AttachmentId,
                        ExpectedVersion = originalVersion, CorrectedText = "Must roll back" }, Cancellation));
            Assert.True(fault.ObservedProtectedSave);
        }
        await using var verification = fixture.CreateAsyncScope();
        var stored = await verification.ServiceProvider.GetRequiredService<AppDbContext>().AttachmentTranscripts
            .AsNoTracking().SingleAsync(transcript => transcript.AttachmentId == data.AttachmentId, Cancellation);
        Assert.Equal(originalVersion, stored.Version);
        Assert.Equal("Original", stored.CorrectedText);
        Assert.Null(stored.GeneratedText);
    }

    [Fact]
    public async Task Direct_STT_has_no_DB_protection_and_switch_cancels_late_ignored_result()
    {
        var data = await SeedAsync();
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<SpeechToTextResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedCancellation = default;
        var speech = new DelegateSpeech(token =>
        {
            Assert.Null(database.Database.CurrentTransaction);
            observedCancellation = token;
            entered.TrySetResult();
            return finish.Task;
        });
        var scenario = new AppInteractions.TranscribeAttachment.Scenario(
            scope.ServiceProvider.GetRequiredService<ITranscriptPersistence>(),
            scope.ServiceProvider.GetRequiredService<LocalProfileOperation>(),
            scope.ServiceProvider.GetRequiredService<IAttachmentStorage>(), speech,
            new UploadOptions(), new TranscriptOptions());
        var execution = scenario.ExecuteAsync(new() { InteractionId = data.InteractionId, AttachmentId = data.AttachmentId }, Cancellation);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            await fixture.CreateUserProfileAsync().WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            Assert.True(observedCancellation.IsCancellationRequested);
            finish.TrySetResult(new("Late", "synthetic", "model", "v1"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.Equal(data.OwnerId, scope.ServiceProvider.GetRequiredService<LocalProfileOperation>().RequireOwner());
            await using var verification = fixture.CreateAsyncScope();
            Assert.False(await verification.ServiceProvider.GetRequiredService<AppDbContext>().AttachmentTranscripts
                .AnyAsync(transcript => transcript.AttachmentId == data.AttachmentId, Cancellation));
        }
        finally { finish.TrySetResult(new("Late", "synthetic", "model", "v1")); }
    }

    private async Task<Seed> SeedAsync()
    {
        var ownerId = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        using var content = new MemoryStream([1, 2, 3, 4]);
        var key = await storage.SaveAsync(content, "synthetic.wav", "audio/wav", Cancellation);
        Assert.True(await storage.CompleteUploadAsync(key, Cancellation));
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = ownerId,
            Title = "Synthetic", Description = "Synthetic", OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow };
        var attachment = new InteractionAttachment { Id = Guid.NewGuid(), InteractionId = interaction.Id,
            Type = AttachmentType.Audio, Status = AttachmentStatus.Ready, StorageKey = key, MimeType = "audio/wav", CreatedAt = DateTimeOffset.UtcNow };
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.Interactions.Add(interaction);
        database.InteractionAttachments.Add(attachment);
        await database.SaveChangesAsync(Cancellation);
        return new(ownerId, interaction.Id, attachment.Id);
    }

    private async Task BindAsync(IServiceProvider services)
    {
        var selection = await services.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        var ownerId = Assert.IsType<Guid>(selection.UserProfileId);
        services.GetRequiredService<LocalProfileOperation>().Bind(new(ownerId, selection.Version),
            services.GetRequiredService<LocalProfileLifetimes>().Capture(selection.Version));
    }

    private sealed record Seed(Guid OwnerId, Guid InteractionId, Guid AttachmentId);
    private sealed class SyntheticSaveException : Exception;
    private sealed class DelegateSpeech(Func<CancellationToken, Task<SpeechToTextResult>> execute) : ISpeechToTextClient
    {
        public Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken) => execute(cancellationToken);
    }

    private sealed class FailTranscriptSave : SaveChangesInterceptor
    {
        public bool ObservedProtectedSave { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<AttachmentTranscript>().Any() == true)
            {
                Assert.NotNull(eventData.Context.Database.CurrentTransaction);
                ObservedProtectedSave = true;
                throw new SyntheticSaveException();
            }
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class InteractionApplicationTests(PeopleApiFixture fixture) : InteractionApplicationTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteInteractionApplicationTests(SqliteApiFixture fixture) : InteractionApplicationTestsContract<SqliteApiFixture>(fixture);
