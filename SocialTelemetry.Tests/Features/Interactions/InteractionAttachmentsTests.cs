using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Tests.Features.People;
using AddAttachment = SocialTelemetry.Api.Features.Interactions.AddAttachment;
using CreateInteraction = SocialTelemetry.Api.Features.Interactions.Create;
using CreatePerson = SocialTelemetry.Api.Features.People.Create;
using GetAttachment = SocialTelemetry.Api.Features.Interactions.GetAttachment;

namespace SocialTelemetry.Tests.Features.Interactions;

public abstract class InteractionAttachmentsTestsContract<TFixture> : IClassFixture<TFixture> where TFixture : PeopleApiFixture
{
    private readonly PeopleApiFixture fixture;

    protected InteractionAttachmentsTestsContract(TFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Add_text_attachment_persists_content()
    {
        var interactionId = await CreateInteractionAsync();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(AttachmentType.Text.ToString()), "Type" },
            { new StringContent("They said they were running late."), "TextContent" }
        };

        using var response = await fixture.Client.PostAsync($"/interactions/{interactionId}/attachments", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var attachment = await response.Content.ReadFromJsonAsync<AddAttachment.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(attachment);
        Assert.Equal(AttachmentType.Text, attachment.Type);
        Assert.Equal(AttachmentStatus.Ready, attachment.Status);
        Assert.Equal("They said they were running late.", attachment.TextContent);
        Assert.Null(attachment.StorageKey);

        var persistedAttachment = await fixture.FindAttachmentAsync(attachment.Id);
        Assert.NotNull(persistedAttachment);
        Assert.Equal("They said they were running late.", persistedAttachment.TextContent);
        Assert.Equal(AttachmentStatus.Ready, persistedAttachment.Status);
    }

    [Fact]
    public async Task Add_file_attachment_persists_metadata_and_writes_file()
    {
        var interactionId = await CreateInteractionAsync();
        var fileBytes = new byte[] { 1, 2, 3, 4 };
        using var form = CreateFileForm(AttachmentType.Image, fileBytes, "photo.png", "image/png");

        using var response = await fixture.Client.PostAsync($"/interactions/{interactionId}/attachments", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var attachment = await response.Content.ReadFromJsonAsync<AddAttachment.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(attachment);
        Assert.Equal(AttachmentType.Image, attachment.Type);
        Assert.Equal(AttachmentStatus.Ready, attachment.Status);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.NotNull(attachment.StorageKey);

        var persistedAttachment = await fixture.FindAttachmentAsync(attachment.Id);
        Assert.NotNull(persistedAttachment);
        Assert.Equal(attachment.StorageKey, persistedAttachment.StorageKey);
        Assert.Equal("image/png", persistedAttachment.MimeType);
        Assert.Equal(AttachmentStatus.Ready, persistedAttachment.Status);

        var storedFilePath = Path.Combine(fixture.AttachmentStorageDirectory, attachment.StorageKey);
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(storedFilePath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(fixture.AttachmentStorageDirectory, ".staging", attachment.StorageKey)));
    }

    [Fact]
    public async Task Add_attachment_returns_not_found_for_missing_interaction()
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(AttachmentType.Text.ToString()), "Type" },
            { new StringContent("No interaction exists."), "TextContent" }
        };

        using var response = await fixture.Client.PostAsync($"/interactions/{Guid.NewGuid()}/attachments", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Add_attachment_rejects_text_file_combination()
    {
        var interactionId = await CreateInteractionAsync();
        using var form = CreateFileForm(AttachmentType.Text, [1], "note.txt", "text/plain");

        using var response = await fixture.Client.PostAsync($"/interactions/{interactionId}/attachments", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Add_attachment_rejects_unsupported_type()
    {
        var interactionId = await CreateInteractionAsync();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("99"), "Type" },
            { new StringContent("Unsupported attachment"), "TextContent" }
        };

        using var response = await fixture.Client.PostAsync($"/interactions/{interactionId}/attachments", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Add_file_attachment_uses_a_storage_path_inside_the_configured_directory()
    {
        var interactionId = await CreateInteractionAsync();
        var unrelatedFilePath = Path.Combine(fixture.AttachmentStorageDirectory, "unrelated.txt");
        await File.WriteAllTextAsync(unrelatedFilePath, "keep this file", TestContext.Current.CancellationToken);

        using var form = CreateFileForm(AttachmentType.Screenshot, [5, 6], "../../outside.png", "image/png");
        using var response = await fixture.Client.PostAsync($"/interactions/{interactionId}/attachments", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var attachment = await response.Content.ReadFromJsonAsync<AddAttachment.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(attachment);
        Assert.NotNull(attachment.StorageKey);
        Assert.DoesNotContain("..", attachment.StorageKey);

        var storedFilePath = Path.GetFullPath(Path.Combine(fixture.AttachmentStorageDirectory, attachment.StorageKey));
        var relativePath = Path.GetRelativePath(fixture.AttachmentStorageDirectory, storedFilePath);

        Assert.False(Path.IsPathRooted(relativePath));
        Assert.False(relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.True(File.Exists(storedFilePath));
        Assert.True(File.Exists(unrelatedFilePath));
        Assert.Equal("keep this file", await File.ReadAllTextAsync(unrelatedFilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_attachment_returns_text_metadata_and_content()
    {
        var interactionId = await CreateInteractionAsync();
        var attachment = await AddTextAttachmentAsync(interactionId, "They called after work.");

        using var response = await fixture.Client.GetAsync($"/interactions/{interactionId}/attachments/{attachment.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<GetAttachment.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal(attachment.Id, metadata.Id);
        Assert.Equal(AttachmentType.Text, metadata.Type);
        Assert.Equal("They called after work.", metadata.TextContent);
        Assert.Null(metadata.MimeType);
    }

    [Fact]
    public async Task Get_attachment_returns_file_metadata_without_text_content()
    {
        var interactionId = await CreateInteractionAsync();
        var attachment = await AddFileAttachmentAsync(interactionId, AttachmentType.Audio, [7, 8], "audio/mpeg");

        using var response = await fixture.Client.GetAsync($"/interactions/{interactionId}/attachments/{attachment.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<GetAttachment.Response>(TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal(AttachmentType.Audio, metadata.Type);
        Assert.Equal("audio/mpeg", metadata.MimeType);
        Assert.Null(metadata.TextContent);
    }

    [Fact]
    public async Task Download_attachment_returns_stored_bytes_and_mime_type()
    {
        var interactionId = await CreateInteractionAsync();
        var fileBytes = new byte[] { 9, 10, 11 };
        var attachment = await AddFileAttachmentAsync(interactionId, AttachmentType.Image, fileBytes, "image/png");

        using var response = await fixture.Client.GetAsync($"/interactions/{interactionId}/attachments/{attachment.Id}/content", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(fileBytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_text_attachment_is_rejected()
    {
        var interactionId = await CreateInteractionAsync();
        var attachment = await AddTextAttachmentAsync(interactionId, "This is database content.");

        using var response = await fixture.Client.GetAsync($"/interactions/{interactionId}/attachments/{attachment.Id}/content", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Attachment_cannot_be_retrieved_through_another_interaction()
    {
        var firstInteractionId = await CreateInteractionAsync();
        var secondInteractionId = await CreateInteractionAsync();
        var attachment = await AddTextAttachmentAsync(firstInteractionId, "Only on the first interaction.");

        using var response = await fixture.Client.GetAsync($"/interactions/{secondInteractionId}/attachments/{attachment.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_text_attachment_removes_database_record()
    {
        var interactionId = await CreateInteractionAsync();
        var attachment = await AddTextAttachmentAsync(interactionId, "Temporary note.");

        using var response = await fixture.Client.DeleteAsync($"/interactions/{interactionId}/attachments/{attachment.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await fixture.FindAttachmentAsync(attachment.Id));
    }

    [Fact]
    public async Task Delete_file_attachment_removes_database_record_and_stored_file()
    {
        var interactionId = await CreateInteractionAsync();
        var attachment = await AddFileAttachmentAsync(interactionId, AttachmentType.Screenshot, [12, 13], "image/png");
        var filePath = Path.Combine(fixture.AttachmentStorageDirectory, attachment.StorageKey!);

        using var response = await fixture.Client.DeleteAsync($"/interactions/{interactionId}/attachments/{attachment.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await fixture.FindAttachmentAsync(attachment.Id));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task Delete_returns_not_found_for_missing_attachment()
    {
        var interactionId = await CreateInteractionAsync();

        using var response = await fixture.Client.DeleteAsync($"/interactions/{interactionId}/attachments/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_file_attachment_does_not_remove_another_attachment_file()
    {
        var interactionId = await CreateInteractionAsync();
        var firstAttachment = await AddFileAttachmentAsync(interactionId, AttachmentType.Image, [1, 2], "image/png");
        var secondAttachment = await AddFileAttachmentAsync(interactionId, AttachmentType.Image, [3, 4], "image/png");
        var firstFilePath = Path.Combine(fixture.AttachmentStorageDirectory, firstAttachment.StorageKey!);
        var secondFilePath = Path.Combine(fixture.AttachmentStorageDirectory, secondAttachment.StorageKey!);

        using var response = await fixture.Client.DeleteAsync($"/interactions/{interactionId}/attachments/{firstAttachment.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(File.Exists(firstFilePath));
        Assert.True(File.Exists(secondFilePath));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(secondFilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_interaction_removes_its_files_and_preserves_unrelated_files()
    {
        var interactionId = await CreateInteractionAsync();
        var otherInteractionId = await CreateInteractionAsync();
        var attachment = await AddFileAttachmentAsync(interactionId, AttachmentType.Image, [1], "image/png");
        var otherAttachment = await AddFileAttachmentAsync(otherInteractionId, AttachmentType.Image, [2], "image/png");

        using var response = await fixture.Client.DeleteAsync($"/interactions/{interactionId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await fixture.FindAttachmentAsync(attachment.Id));
        Assert.False(File.Exists(Path.Combine(fixture.AttachmentStorageDirectory, attachment.StorageKey!)));
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(Path.Combine(fixture.AttachmentStorageDirectory, otherAttachment.StorageKey!), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_file_returns_not_found_and_can_still_be_deleted()
    {
        var interactionId = await CreateInteractionAsync();
        var attachment = await AddFileAttachmentAsync(interactionId, AttachmentType.Image, [1], "image/png");
        File.Delete(Path.Combine(fixture.AttachmentStorageDirectory, attachment.StorageKey!));

        using var downloadResponse = await fixture.Client.GetAsync($"/interactions/{interactionId}/attachments/{attachment.Id}/content", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, downloadResponse.StatusCode);

        using var deleteResponse = await fixture.Client.DeleteAsync($"/interactions/{interactionId}/attachments/{attachment.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        Assert.Null(await fixture.FindAttachmentAsync(attachment.Id));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failed_metadata_save_removes_uploaded_file(int failedSaveCall)
    {
        var interactionId = await CreateInteractionAsync();
        var existingFiles = Directory.GetFiles(fixture.AttachmentStorageDirectory, "*", SearchOption.AllDirectories).Order().ToArray();
        using var application = fixture.WithServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new FailedSaveInterceptor(failedSaveCall))));
        using var client = application.CreateClient();
        using var form = CreateFileForm(AttachmentType.Image, [1], "private.png", "image/png");

        using var response = await client.PostAsync($"/interactions/{interactionId}/attachments", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(existingFiles, Directory.GetFiles(fixture.AttachmentStorageDirectory, "*", SearchOption.AllDirectories).Order().ToArray());
        await using var scope = fixture.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await dbContext.InteractionAttachments.AsNoTracking()
            .AnyAsync(attachment => attachment.InteractionId == interactionId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_file_delete_keeps_metadata_for_retry()
    {
        var interactionId = await CreateInteractionAsync();
        var attachment = await AddFileAttachmentAsync(interactionId, AttachmentType.Image, [1], "image/png");
        using var application = fixture.WithServices(services =>
        {
            services.AddScoped<LocalAttachmentStorage>();
            services.AddScoped<IAttachmentStorage>(serviceProvider =>
                new FailedDeleteStorage(serviceProvider.GetRequiredService<LocalAttachmentStorage>()));
        });
        using var client = application.CreateClient();

        using var failedResponse = await client.DeleteAsync($"/interactions/{interactionId}/attachments/{attachment.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, failedResponse.StatusCode);
        var failedAttachment = await fixture.FindAttachmentAsync(attachment.Id);
        Assert.NotNull(failedAttachment);
        Assert.Equal(AttachmentStatus.Deleting, failedAttachment.Status);
        Assert.True(File.Exists(Path.Combine(fixture.AttachmentStorageDirectory, attachment.StorageKey!)));

        using var retryResponse = await fixture.Client.DeleteAsync($"/interactions/{interactionId}/attachments/{attachment.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Null(await fixture.FindAttachmentAsync(attachment.Id));
    }

    [Fact]
    public async Task Storage_rejects_invalid_keys_without_touching_unrelated_files()
    {
        using var application = fixture.WithServices(_ => { });
        using var scope = application.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        var unrelatedFile = Path.Combine(fixture.AttachmentStorageDirectory, "keep.txt");
        await File.WriteAllTextAsync(unrelatedFile, "Unrelated content", TestContext.Current.CancellationToken);

        foreach (var key in new[] { "../keep.txt", "..\\keep.txt", "keep.txt", unrelatedFile })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.OpenReadAsync(key, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteAsync(key, CancellationToken.None));
        }

        Assert.Equal("Unrelated content", await File.ReadAllTextAsync(unrelatedFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_storage_write_removes_partial_file()
    {
        using var application = fixture.WithServices(_ => { });
        using var scope = application.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        var existingFiles = Directory.GetFiles(fixture.AttachmentStorageDirectory, "*", SearchOption.AllDirectories).Order().ToArray();
        await using var content = new FailedReadStream();

        await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync(content, "private.png", "image/png", CancellationToken.None));

        Assert.Equal(existingFiles, Directory.GetFiles(fixture.AttachmentStorageDirectory, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    private sealed class FailedReadStream : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1 }, cancellationToken);
            throw new IOException("Simulated read failure after a partial write.");
        }
    }

    private sealed class FailedSaveInterceptor(int failedSaveCall) : SaveChangesInterceptor
    {
        private int saveCallCount;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            saveCallCount++;
            if (saveCallCount == failedSaveCall)
            {
                throw new InvalidOperationException("Simulated metadata save failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailedDeleteStorage(IAttachmentStorage storage) : IAttachmentStorage
    {
        public Task<string> SaveAsync(Stream content, string fileName, string? mimeType, CancellationToken cancellationToken) =>
            storage.SaveAsync(content, fileName, mimeType, cancellationToken);

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
            storage.OpenReadAsync(storageKey, cancellationToken);

        public Task<bool> CompleteUploadAsync(string storageKey, CancellationToken cancellationToken) =>
            storage.CompleteUploadAsync(storageKey, cancellationToken);

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken) =>
            storage.ExistsAsync(storageKey, cancellationToken);

        public Task DeleteStagedAsync(string storageKey, CancellationToken cancellationToken) =>
            storage.DeleteStagedAsync(storageKey, cancellationToken);

        public IEnumerable<StoredAttachmentFile> EnumerateFiles() => storage.EnumerateFiles();
        public IEnumerable<StoredAttachmentFile> EnumerateStagedFiles() => storage.EnumerateStagedFiles();

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) =>
            throw new IOException("Simulated file deletion failure.");
    }

    private async Task<Guid> CreateInteractionAsync()
    {
        var userProfileId = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(new CreatePerson.Request
        {
            UserProfileId = userProfileId,
            DisplayName = "Morgan",
            RelationshipContext = RelationshipContext.Acquaintance
        });
        var request = new CreateInteraction.Request
        {
            UserProfileId = userProfileId,
            Title = "Test interaction",
            Description = "Test description",
            OccurredAt = DateTimeOffset.UtcNow,
            ParticipantIds = [personId]
        };

        using var response = await fixture.Client.PostAsJsonAsync("/interactions", request);
        response.EnsureSuccessStatusCode();

        var interaction = await response.Content.ReadFromJsonAsync<CreateInteraction.Response>();
        return interaction!.Id;
    }

    private async Task<AddAttachment.Response> AddTextAttachmentAsync(Guid interactionId, string textContent)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(AttachmentType.Text.ToString()), "Type" },
            { new StringContent(textContent), "TextContent" }
        };
        using var response = await fixture.Client.PostAsync($"/interactions/{interactionId}/attachments", form);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AddAttachment.Response>())!;
    }

    private async Task<AddAttachment.Response> AddFileAttachmentAsync(
        Guid interactionId,
        AttachmentType attachmentType,
        byte[] content,
        string mimeType)
    {
        using var form = CreateFileForm(attachmentType, content, "attachment.bin", mimeType);
        using var response = await fixture.Client.PostAsync($"/interactions/{interactionId}/attachments", form);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AddAttachment.Response>())!;
    }

    private static MultipartFormDataContent CreateFileForm(
        AttachmentType attachmentType,
        byte[] content,
        string fileName,
        string mimeType)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(attachmentType.ToString()), "Type" }
        };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(file, "File", fileName);

        return form;
    }
}

public sealed class InteractionAttachmentsTests(PeopleApiFixture fixture) : InteractionAttachmentsTestsContract<PeopleApiFixture>(fixture);

[Collection("SQLite")]
public sealed class SqliteInteractionAttachmentsTests(SqliteApiFixture fixture) : InteractionAttachmentsTestsContract<SqliteApiFixture>(fixture);
