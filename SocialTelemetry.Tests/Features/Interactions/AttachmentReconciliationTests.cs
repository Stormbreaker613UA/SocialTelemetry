using System.Net;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Infrastructure.Profiles;
using UploadAttachment = SocialTelemetry.Application.Features.Interactions.AddAttachment;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Storage;
using SocialTelemetry.Tests.Features.People;
using CreateInteraction = SocialTelemetry.Api.Features.Interactions.Create;
using CreatePerson = SocialTelemetry.Api.Features.People.Create;

namespace SocialTelemetry.Tests.Features.Interactions;

public abstract class AttachmentReconciliationTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture> where TFixture : PeopleApiFixture
{
    [Theory]
    [InlineData("staging")]
    [InlineData("final")]
    [InlineData("missing")]
    public async Task Pending_attachments_are_completed_or_removed(string fileLocation)
    {
        var attachment = await CreateAttachmentAsync(AttachmentStatus.Pending);
        if (fileLocation != "missing")
        {
            await WriteFileAsync(attachment.StorageKey!, staged: fileLocation == "staging");
        }

        using var beforeRecovery = await fixture.Client.GetAsync(
            $"/interactions/{attachment.InteractionId}/attachments/{attachment.Id}/content", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, beforeRecovery.StatusCode);

        await ReconcileAsync();
        await ReconcileAsync();

        var recoveredAttachment = await fixture.FindAttachmentAsync(attachment.Id);
        if (fileLocation == "missing")
        {
            Assert.Null(recoveredAttachment);
            return;
        }

        Assert.NotNull(recoveredAttachment);
        Assert.Equal(AttachmentStatus.Ready, recoveredAttachment.Status);
        Assert.False(File.Exists(GetFilePath(attachment.StorageKey!, staged: true)));
        using var download = await fixture.Client.GetAsync(
            $"/interactions/{attachment.InteractionId}/attachments/{attachment.Id}/content", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleting_attachments_are_removed_even_when_file_is_missing(bool fileExists)
    {
        var attachment = await CreateAttachmentAsync(AttachmentStatus.Deleting);
        if (fileExists)
        {
            await WriteFileAsync(attachment.StorageKey!, staged: false);
            await WriteFileAsync(attachment.StorageKey!, staged: true);
        }

        await ReconcileAsync();
        await ReconcileAsync();

        Assert.Null(await fixture.FindAttachmentAsync(attachment.Id));
        Assert.False(File.Exists(GetFilePath(attachment.StorageKey!, staged: false)));
        Assert.False(File.Exists(GetFilePath(attachment.StorageKey!, staged: true)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Only_old_unreferenced_files_are_removed(bool staged, bool old)
    {
        var storageKey = Guid.NewGuid().ToString("N");
        await WriteFileAsync(storageKey, staged);
        var filePath = GetFilePath(storageKey, staged);
        if (old)
        {
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddHours(-2));
        }

        await ReconcileAsync();

        Assert.Equal(!old, File.Exists(filePath));
    }

    [Fact]
    public async Task Ready_attachment_with_missing_file_is_preserved_and_warned_about()
    {
        var attachment = await CreateAttachmentAsync(AttachmentStatus.Ready);
        var logger = new CapturedRecoveryLogger();

        await ReconcileAsync(logger);

        var preservedAttachment = await fixture.FindAttachmentAsync(attachment.Id);
        Assert.NotNull(preservedAttachment);
        Assert.Equal(AttachmentStatus.Ready, preservedAttachment.Status);
        Assert.Contains(logger.Warnings, warning => warning.Contains(attachment.Id.ToString()) && warning.Contains("missing"));
    }

    [Fact]
    public async Task Old_referenced_file_and_unrelated_filename_are_preserved()
    {
        var attachment = await CreateAttachmentAsync(AttachmentStatus.Ready);
        await WriteFileAsync(attachment.StorageKey!, staged: false);
        File.SetLastWriteTimeUtc(GetFilePath(attachment.StorageKey!, staged: false), DateTime.UtcNow.AddHours(-2));
        var unrelatedFile = Path.Combine(fixture.AttachmentStorageDirectory, "keep.txt");
        await File.WriteAllTextAsync(unrelatedFile, "Unrelated data", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(unrelatedFile, DateTime.UtcNow.AddHours(-2));

        await ReconcileAsync();

        Assert.True(File.Exists(GetFilePath(attachment.StorageKey!, staged: false)));
        Assert.Equal("Unrelated data", await File.ReadAllTextAsync(unrelatedFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Application_startup_recovers_pending_upload()
    {
        var attachment = await CreateAttachmentAsync(AttachmentStatus.Pending);
        await WriteFileAsync(attachment.StorageKey!, staged: true);

        using var restartedApplication = fixture.WithServices(_ => { });
        using var client = restartedApplication.CreateClient();
        using var response = await client.GetAsync(
            $"/interactions/{attachment.InteractionId}/attachments/{attachment.Id}/content", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var recoveredAttachment = await fixture.FindAttachmentAsync(attachment.Id);
        Assert.NotNull(recoveredAttachment);
        Assert.Equal(AttachmentStatus.Ready, recoveredAttachment.Status);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("switch")]
    [InlineData("reparent")]
    [InlineData("deleting")]
    [InlineData("deleted")]
    [InlineData("pending")]
    public async Task Active_upload_cooperates_with_other_host_recovery_without_destroying_its_ready_result(string outcome)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var interactionId = await CreateInteractionAsync();
        var pause = new UploadPause();
        using var recoveryHost = fixture.WithServices(_ => { });
        using var recoveryClient = recoveryHost.CreateClient();
        using var uploadHost = fixture.WithServices(services =>
        {
            services.AddScoped<LocalAttachmentStorage>();
            services.AddScoped<IAttachmentStorage>(provider => new PausedUploadStorage(
                provider.GetRequiredService<LocalAttachmentStorage>(), provider.GetRequiredService<AppDbContext>(), pause, outcome == "io"));
        });
        using var uploadClient = uploadHost.CreateClient();
        await using var active = uploadHost.Services.CreateAsyncScope();
        var services = active.ServiceProvider;
        var selected = await services.GetRequiredService<LocalProfileSelectionService>().GetAsync(cancellation);
        await services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
            .ExecuteAsync(selected.UserProfileId, selected.Version, cancellation);
        var database = services.GetRequiredService<AppDbContext>();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var bytes = new byte[] { 3, 1, 4, 1, 5 };
        var upload = services.GetRequiredService<UploadAttachment.Scenario>().ExecuteAsync(new(interactionId,
            AttachmentType.Image, null, new(bytes.Length, "synthetic.png", "image/png", () => new MemoryStream(bytes))), caller.Token);
        try
        {
            var key = await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            InteractionAttachment original;
            await using (var checking = fixture.CreateAsyncScope())
            {
                original = await checking.ServiceProvider.GetRequiredService<AppDbContext>().InteractionAttachments
                    .AsNoTracking().SingleAsync(row => row.InteractionId == interactionId, cancellation);
                Assert.Equal(AttachmentStatus.Pending, original.Status);
                Assert.Equal(key, original.StorageKey);
                Assert.True(File.Exists(GetFilePath(key, staged: true)));
            }
            var recovery = new AttachmentReconciliationService(recoveryHost.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AttachmentReconciliationService>.Instance, recoveryHost.Services.GetRequiredService<IOptions<StorageMaintenanceOptions>>());
            await recovery.ReconcileAsync(cancellation);
            Assert.Equal(AttachmentStatus.Ready, (await fixture.FindAttachmentAsync(original.Id))!.Status);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(GetFilePath(key, staged: false), cancellation));

            Person? callerPerson = null;
            string? originalNotes = null;
            if (outcome == "pending")
            {
                callerPerson = await database.People.SingleAsync(person => person.UserProfileId == selected.UserProfileId, cancellation);
                originalNotes = callerPerson.Notes;
                callerPerson.Notes = "Uncommitted caller work";
            }
            if (outcome == "cancel") caller.Cancel();
            if (outcome == "switch") await fixture.CreateUserProfileAsync();
            if (outcome == "deleted")
            {
                using var deleted = await fixture.Client.DeleteAsync($"/interactions/{interactionId}/attachments/{original.Id}", cancellation);
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            }
            if (outcome is "reparent" or "deleting")
            {
                await using var changing = fixture.CreateAsyncScope();
                var other = changing.ServiceProvider.GetRequiredService<AppDbContext>();
                var row = await other.InteractionAttachments.SingleAsync(row => row.Id == original.Id, cancellation);
                if (outcome == "reparent")
                {
                    var destination = new Interaction { Id = Guid.NewGuid(), UserProfileId = selected.UserProfileId!.Value, Title = "Same-owner destination" };
                    other.Interactions.Add(destination);
                    row.InteractionId = destination.Id;
                }
                else row.Status = AttachmentStatus.Deleting;
                await other.SaveChangesAsync(cancellation);
            }
            pause.Resume.TrySetResult();
            if (outcome == "success")
            {
                var completed = await upload;
                Assert.Equal(original.Id, completed.Id);
                Assert.Equal(AttachmentStatus.Ready, completed.Status);
            }
            else if (outcome is "io" or "deleted") await Assert.ThrowsAsync<IOException>(() => upload);
            else if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
            else if (outcome == "switch")
            {
                var exception = await Record.ExceptionAsync(() => upload);
                Assert.True(exception is OperationCanceledException or LocalProfileAccessException);
            }
            else await Assert.ThrowsAsync<ConflictException>(() => upload);

            await using var verification = fixture.CreateAsyncScope();
            var stored = verification.ServiceProvider.GetRequiredService<AppDbContext>();
            var retained = await stored.InteractionAttachments.AsNoTracking().SingleOrDefaultAsync(row => row.Id == original.Id, cancellation);
            if (outcome is "deleting" or "deleted") Assert.Null(retained);
            else
            {
                Assert.NotNull(retained);
                Assert.Equal(AttachmentStatus.Ready, retained.Status);
                Assert.Equal(key, retained.StorageKey);
                Assert.Equal(outcome == "reparent", retained.InteractionId != interactionId);
                Assert.Equal(1, await stored.InteractionAttachments.CountAsync(row => row.StorageKey == key, cancellation));
                Assert.Equal(bytes, await File.ReadAllBytesAsync(GetFilePath(key, staged: false), cancellation));
            }
            if (callerPerson is not null)
            {
                Assert.Equal(originalNotes, await stored.People.Where(person => person.Id == callerPerson.Id).Select(person => person.Notes).SingleAsync(cancellation));
                Assert.Equal(EntityState.Modified, database.Entry(callerPerson).State);
                Assert.Equal("Uncommitted caller work", callerPerson.Notes);
            }
        }
        finally
        {
            pause.Resume.TrySetResult();
            if (!upload.IsCompleted) await Record.ExceptionAsync(() => upload);
        }
    }

    private sealed class UploadPause
    {
        public TaskCompletionSource<string> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PausedUploadStorage(IAttachmentStorage storage, AppDbContext database, UploadPause pause, bool failCompletion) : IAttachmentStorage
    {
        public Task<string> SaveAsync(Stream content, string name, string? mime, CancellationToken token) => storage.SaveAsync(content, name, mime, token);
        public async Task<bool> CompleteUploadAsync(string key, CancellationToken token)
        {
            Assert.Null(database.Database.CurrentTransaction);
            Assert.Equal(AttachmentStatus.Pending, await database.InteractionAttachments.AsNoTracking()
                .Where(row => row.StorageKey == key).Select(row => row.Status).SingleAsync(token));
            pause.Entered.TrySetResult(key);
            await pause.Resume.Task;
            token.ThrowIfCancellationRequested();
            if (failCompletion) throw new IOException("Synthetic completion failure.");
            return await storage.CompleteUploadAsync(key, token);
        }
        public Task<bool> ExistsAsync(string key, CancellationToken token) => storage.ExistsAsync(key, token);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken token) => storage.OpenReadAsync(key, token);
        public Task DeleteAsync(string key, CancellationToken token) => storage.DeleteAsync(key, token);
        public Task DeleteStagedAsync(string key, CancellationToken token) => storage.DeleteStagedAsync(key, token);
        public IEnumerable<StoredAttachmentFile> EnumerateFiles() => storage.EnumerateFiles();
        public IEnumerable<StoredAttachmentFile> EnumerateStagedFiles() => storage.EnumerateStagedFiles();
    }

    private async Task ReconcileAsync(ILogger<AttachmentReconciliationService>? logger = null)
    {
        await using var scope = fixture.CreateAsyncScope();
        var service = new AttachmentReconciliationService(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            logger ?? NullLogger<AttachmentReconciliationService>.Instance,
            scope.ServiceProvider.GetRequiredService<IOptions<StorageMaintenanceOptions>>());
        await service.ReconcileAsync(CancellationToken.None);
    }

    private async Task<Guid> CreateInteractionAsync()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(new CreatePerson.Request
        {
            UserProfileId = userProfileId,
            DisplayName = "Recovery test participant"
        });
        using var interactionResponse = await fixture.Client.PostAsJsonAsync("/interactions", new CreateInteraction.Request
        {
            UserProfileId = userProfileId,
            Title = "Recovery test",
            Description = "Test interaction",
            OccurredAt = DateTimeOffset.UtcNow,
            ParticipantIds = [personId]
        });
        interactionResponse.EnsureSuccessStatusCode();
        var interaction = await interactionResponse.Content.ReadFromJsonAsync<CreateInteraction.Response>();
        Assert.NotNull(interaction);
        return interaction.Id;

    }

    private async Task<InteractionAttachment> CreateAttachmentAsync(AttachmentStatus status)
    {
        var interactionId = await CreateInteractionAsync();
        var attachment = new InteractionAttachment
        {
            Id = Guid.NewGuid(),
            InteractionId = interactionId,
            Type = AttachmentType.Image,
            Status = status,
            StorageKey = Guid.NewGuid().ToString("N"),
            MimeType = "image/png",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await using var scope = fixture.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.InteractionAttachments.Add(attachment);
        await dbContext.SaveChangesAsync();
        return attachment;
    }

    private async Task WriteFileAsync(string storageKey, bool staged)
    {
        var filePath = GetFilePath(storageKey, staged);
        var directory = staged
            ? Path.Combine(fixture.AttachmentStorageDirectory, ".staging")
            : fixture.AttachmentStorageDirectory;
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3]);
    }

    private string GetFilePath(string storageKey, bool staged) => staged
        ? Path.Combine(fixture.AttachmentStorageDirectory, ".staging", storageKey)
        : Path.Combine(fixture.AttachmentStorageDirectory, storageKey);

    private sealed class CapturedRecoveryLogger : ILogger<AttachmentReconciliationService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

public sealed class AttachmentReconciliationTests(PeopleApiFixture fixture) : AttachmentReconciliationTestsContract<PeopleApiFixture>(fixture);

[Collection("SQLite")]
public sealed class SqliteAttachmentReconciliationTests(SqliteApiFixture fixture) : AttachmentReconciliationTestsContract<SqliteApiFixture>(fixture);
