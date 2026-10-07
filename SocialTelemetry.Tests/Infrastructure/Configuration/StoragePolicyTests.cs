using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Infrastructure.Configuration;

public sealed class StoragePolicyTests(PeopleApiFixture fixture) : IClassFixture<PeopleApiFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Configured_attachment_limit_is_enforced_before_storing_file()
    {
        var profileId = await fixture.CreateUserProfileAsync();
        var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = profileId, Title = "Synthetic upload", Description = "Synthetic" };
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.Interactions.Add(interaction);
            await database.SaveChangesAsync(Cancellation);
        }
        using var application = fixture.WithServices(services => services.Configure<UploadOptions>(options => options.AttachmentMaxBytes = 10));
        using var client = application.CreateClient();
        foreach (var size in new[] { 10, 11 })
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Image"), "Type");
            var file = new ByteArrayContent(new byte[size]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "File", "synthetic.png");
            using var response = await client.PostAsync($"/interactions/{interaction.Id}/attachments", form, Cancellation);
            Assert.Equal(size == 10 ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("10 MB", await response.Content.ReadAsStringAsync(Cancellation));
        }
        await using var verification = application.Services.CreateAsyncScope();
        var storage = verification.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        Assert.Single(storage.EnumerateFiles());
    }

    [Theory]
    [InlineData("attachments", false)]
    [InlineData("attachments", true)]
    [InlineData("avatars", false)]
    [InlineData("avatars", true)]
    public async Task Both_reconciliation_services_use_configured_age(string area, bool staged)
    {
        using var application = fixture.WithServices(services => services.Configure<StorageMaintenanceOptions>(options => options.OrphanSafetyAge = TimeSpan.FromHours(3)));
        using var client = application.CreateClient();
        await using var scope = application.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredKeyedService<IFileStorage>(area);
        using var bytes = new MemoryStream([1, 2, 3]);
        var key = await storage.SaveAsync(bytes, Cancellation);
        if (!staged) Assert.True(await storage.CompleteUploadAsync(key, Cancellation));
        var directory = area == "avatars" ? Path.Combine(fixture.AttachmentStorageDirectory, "avatars") : fixture.AttachmentStorageDirectory;
        var path = staged ? Path.Combine(directory, ".staging", key) : Path.Combine(directory, key);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        // The old fixed one-hour cutoff would incorrectly remove this file.
        using (var restarted = fixture.WithServices(services => services.Configure<StorageMaintenanceOptions>(options => options.OrphanSafetyAge = TimeSpan.FromHours(3))))
        using (restarted.CreateClient()) Assert.True(File.Exists(path));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-4));
        using (var restarted = fixture.WithServices(services => services.Configure<StorageMaintenanceOptions>(options => options.OrphanSafetyAge = TimeSpan.FromHours(3))))
        using (restarted.CreateClient()) Assert.False(File.Exists(path));
    }
}
