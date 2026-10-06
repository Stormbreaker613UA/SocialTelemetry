using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Tests.Features.People;
using CreateInteraction = SocialTelemetry.Api.Features.Interactions.Create;
using CreatePerson = SocialTelemetry.Api.Features.People.Create;

namespace SocialTelemetry.Tests.Features.Interactions;

public sealed class AttachmentReconciliationTests(PeopleApiFixture fixture) : IClassFixture<PeopleApiFixture>
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

    private async Task ReconcileAsync(ILogger<AttachmentReconciliationService>? logger = null)
    {
        await using var scope = fixture.CreateAsyncScope();
        var service = new AttachmentReconciliationService(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            logger ?? NullLogger<AttachmentReconciliationService>.Instance);
        await service.ReconcileAsync(CancellationToken.None);
    }

    private async Task<InteractionAttachment> CreateAttachmentAsync(AttachmentStatus status)
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

        var attachment = new InteractionAttachment
        {
            Id = Guid.NewGuid(),
            InteractionId = interaction.Id,
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
