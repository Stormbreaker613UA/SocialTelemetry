using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Domain.Interactions;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class ProfileAvatarTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture> where TFixture : PeopleApiFixture
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1];
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private UploadOptions UploadPolicy
    {
        get
        {
            using var scope = fixture.CreateAsyncScope();
            return scope.ServiceProvider.GetRequiredService<IOptions<UploadOptions>>().Value;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configured_avatar_limit_is_enforced_for_both_owners(bool person)
    {
        var (route, _) = await CreateOwnerAsync(person);
        using var application = fixture.WithServices(services => services.Configure<UploadOptions>(options => options.AvatarMaxBytes = Png.Length));
        using var client = application.CreateClient();
        await UploadAsync(client, route, Png);
        using var form = Form(Png.Concat(new byte[] { 1 }).ToArray(), "image/png");
        using var response = await client.PutAsync(route, form, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("5 MiB", await response.Content.ReadAsStringAsync(Cancellation));
        using var download = await client.GetAsync(route, Cancellation);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync(Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_download_replace_and_delete(bool person)
    {
        var (route, _) = await CreateOwnerAsync(person);
        await UploadAsync(fixture.Client, route, Png);
        var firstKey = await KeyAsync(route);
        using var download = await fixture.Client.GetAsync(route, Cancellation);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync(Cancellation));
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());

        var replacement = Png.Concat(new byte[] { 2 }).ToArray();
        await UploadAsync(fixture.Client, route, replacement);
        var secondKey = await KeyAsync(route);
        Assert.NotEqual(firstKey, secondKey);
        Assert.False(File.Exists(AvatarPath(firstKey)));
        using var replaced = await fixture.Client.GetAsync(route, Cancellation);
        Assert.Equal(replacement, await replaced.Content.ReadAsByteArrayAsync(Cancellation));

        using var deleted = await fixture.Client.DeleteAsync(route, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(File.Exists(AvatarPath(secondKey)));
        using var missing = await fixture.Client.GetAsync(route, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData(false, "image/svg+xml", false)]
    [InlineData(true, "image/jpeg", false)]
    [InlineData(false, "image/png", true)]
    [InlineData(true, "image/png", true)]
    public async Task Invalid_or_oversized_input_does_not_replace_existing_avatar(bool person, string mime, bool oversized)
    {
        var (route, _) = await CreateOwnerAsync(person);
        await UploadAsync(fixture.Client, route, Png);
        var originalKey = await KeyAsync(route);
        using var form = Form(oversized ? new byte[UploadPolicy.AvatarMaxBytes + 1] : Png, mime);
        using var response = await fixture.Client.PutAsync(route, form, Cancellation);
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(originalKey, await KeyAsync(route));
        Assert.True(File.Exists(AvatarPath(originalKey)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_database_replacement_preserves_old_avatar_and_cleans_new_file(bool person)
    {
        var (route, _) = await CreateOwnerAsync(person);
        await UploadAsync(fixture.Client, route, Png);
        var originalKey = await KeyAsync(route);
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options =>
            options.AddInterceptors(new FailedSave())));
        using var client = application.CreateClient();
        // Startup reconciliation may remove old orphans from earlier cases; capture the upload baseline afterward.
        var originalFiles = Directory.GetFiles(Path.GetDirectoryName(AvatarPath(originalKey))!).Order().ToArray();
        using var form = Form(Png, "image/png");
        using var response = await client.PutAsync(route, form, Cancellation);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(originalKey, await KeyAsync(route));
        Assert.Equal(originalFiles, Directory.GetFiles(Path.GetDirectoryName(AvatarPath(originalKey))!).Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_owner_removes_avatar(bool person)
    {
        var (route, ownerId) = await CreateOwnerAsync(person);
        await UploadAsync(fixture.Client, route, Png);
        var key = await KeyAsync(route);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (person) database.People.Remove(await database.People.SingleAsync(owner => owner.Id == ownerId, Cancellation));
        else database.UserProfiles.Remove(await database.UserProfiles.SingleAsync(owner => owner.Id == ownerId, Cancellation));
        await database.SaveChangesAsync(Cancellation);
        Assert.False(File.Exists(AvatarPath(key)));
    }

    [Fact]
    public async Task Avatar_changes_do_not_change_ai_context_or_fingerprint()
    {
        var (route, personId) = await CreateOwnerAsync(person: true);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var person = await database.People.SingleAsync(owner => owner.Id == personId, Cancellation);
        var interaction = new Interaction
        {
            Id = Guid.NewGuid(), UserProfileId = person.UserProfileId, Title = "Synthetic",
            Description = "A synthetic exchange.", OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            Participants = [new InteractionParticipant { PersonId = personId }]
        };
        database.Interactions.Add(interaction);
        await database.SaveChangesAsync(Cancellation);
        var builder = scope.ServiceProvider.GetRequiredService<AiContextBuilder>();
        var before = await builder.BuildAsync(interaction.Id, null, [], Cancellation);
        await UploadAsync(fixture.Client, route, Png);
        await UploadAsync(fixture.Client, "/user-profile/avatar", Png);
        var after = await builder.BuildAsync(interaction.Id, null, [], Cancellation);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(before.InputJson, after.InputJson);
        Assert.Empty(after.Images);
    }

    [Fact]
    public async Task Profile_cascade_cleans_unloaded_person_avatar_and_preserves_other_profiles()
    {
        var (route, personId) = await CreateOwnerAsync(true);
        await UploadAsync(fixture.Client, route, Png);
        var key = await KeyAsync(route);
        var otherProfile = await fixture.CreateUserProfileAsync();
        var otherPerson = await fixture.CreatePersonAsync(new() { UserProfileId = otherProfile, DisplayName = "Other" });
        var otherRoute = $"/people/{otherPerson}/avatar";
        await UploadAsync(fixture.Client, otherRoute, Png);
        var otherKey = await KeyAsync(otherRoute);
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profileId = await database.People.Where(person => person.Id == personId).Select(person => person.UserProfileId).SingleAsync(Cancellation);
        database.UserProfiles.Remove(await database.UserProfiles.SingleAsync(profile => profile.Id == profileId, Cancellation));
        await database.SaveChangesAsync(Cancellation);
        Assert.False(File.Exists(AvatarPath(key)));
        Assert.True(File.Exists(AvatarPath(otherKey)));
    }

    [Fact]
    public async Task Concurrent_replacement_rejects_stale_owner_without_deleting_winning_file()
    {
        var (_, personId) = await CreateOwnerAsync(true);
        await using var first = fixture.CreateAsyncScope();
        await using var second = fixture.CreateAsyncScope();
        var firstService = first.ServiceProvider.GetRequiredService<ProfileAvatarService>();
        var secondService = second.ServiceProvider.GetRequiredService<ProfileAvatarService>();
        var firstOwner = await firstService.FindOwnerAsync(personId, Cancellation);
        var secondOwner = await secondService.FindOwnerAsync(personId, Cancellation);
        await firstService.UploadAsync(firstOwner, Png, "image/png", Cancellation);
        var winningKey = await KeyAsync($"/people/{personId}/avatar");
        await Assert.ThrowsAsync<SocialTelemetry.Api.Common.Exceptions.ConflictException>(() =>
            secondService.UploadAsync(secondOwner, Png, "image/png", Cancellation));
        Assert.Equal(winningKey, await KeyAsync($"/people/{personId}/avatar"));
        Assert.True(File.Exists(AvatarPath(winningKey)));
    }

    private async Task<(string Route, Guid OwnerId)> CreateOwnerAsync(bool person)
    {
        await fixture.ClearUserProfilesAsync();
        var profileId = await fixture.CreateUserProfileAsync();
        if (!person) return ("/user-profile/avatar", profileId);
        var personId = await fixture.CreatePersonAsync(new() { UserProfileId = profileId, DisplayName = "Synthetic" });
        return ($"/people/{personId}/avatar", personId);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public async Task Other_supported_formats_are_accepted(string mimeType)
    {
        var (route, _) = await CreateOwnerAsync(true);
        var bytes = mimeType == "image/jpeg" ? new byte[] { 255, 216, 255, 1 } : "RIFF1234WEBP"u8.ToArray();
        using var form = Form(bytes, mimeType);
        using var response = await fixture.Client.PutAsync(route, form, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var download = await fixture.Client.GetAsync(route, Cancellation);
        Assert.Equal(mimeType, download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(Cancellation));
    }

    [Fact]
    public async Task Cancelled_save_preserves_old_avatar()
    {
        var (route, personId) = await CreateOwnerAsync(true);
        await UploadAsync(fixture.Client, route, Png);
        var originalKey = await KeyAsync(route);
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options =>
            options.AddInterceptors(new CancelledSave())));
        await using var scope = application.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ProfileAvatarService>();
        var owner = await service.FindOwnerAsync(personId, Cancellation);
        var originalFiles = Directory.GetFiles(Path.GetDirectoryName(AvatarPath(originalKey))!).Order().ToArray();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.UploadAsync(owner, Png, "image/png", Cancellation));
        Assert.Equal(originalKey, await KeyAsync(route));
        Assert.Equal(originalFiles, Directory.GetFiles(Path.GetDirectoryName(AvatarPath(originalKey))!).Order().ToArray());
    }

    [Fact]
    public async Task Startup_recovers_aged_orphans_but_preserves_referenced_and_recent_files()
    {
        var (route, _) = await CreateOwnerAsync(true);
        await UploadAsync(fixture.Client, route, Png);
        var originalKey = await KeyAsync(route);
        await using var scope = fixture.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars");
        using var content = new MemoryStream(Png);
        var oldKey = await storage.SaveAsync(content, Cancellation);
        await storage.CompleteUploadAsync(oldKey, Cancellation);
        content.Position = 0;
        var recentKey = await storage.SaveAsync(content, Cancellation);
        await storage.CompleteUploadAsync(recentKey, Cancellation);
        File.SetLastWriteTimeUtc(AvatarPath(oldKey), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(AvatarPath(originalKey), DateTime.UtcNow.AddHours(-2));
        var recovery = new ProfileAvatarReconciliationService(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            scope.ServiceProvider.GetRequiredService<IOptions<StorageMaintenanceOptions>>());
        await recovery.StartAsync(Cancellation);
        Assert.False(File.Exists(AvatarPath(oldKey)));
        Assert.True(File.Exists(AvatarPath(recentKey)));
        Assert.True(File.Exists(AvatarPath(originalKey)));
    }

    [Fact]
    public async Task Missing_owner_returns_not_found()
    {
        await fixture.ClearUserProfilesAsync();
        foreach (var route in new[] { "/user-profile/avatar", $"/people/{Guid.NewGuid()}/avatar" })
        {
            using var get = await fixture.Client.GetAsync(route, Cancellation);
            using var delete = await fixture.Client.DeleteAsync(route, Cancellation);
            using var form = Form(Png, "image/png");
            using var upload = await fixture.Client.PutAsync(route, form, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        }
    }

    private async Task<string> KeyAsync(string route)
    {
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (route == "/user-profile/avatar")
            return (await database.UserProfiles.OrderBy(profile => profile.Id).Select(profile => profile.AvatarStorageKey).FirstAsync(Cancellation))!;
        var id = Guid.Parse(route.Split('/')[2]);
        return (await database.People.Where(person => person.Id == id).Select(person => person.AvatarStorageKey).SingleAsync(Cancellation))!;
    }

    private string AvatarPath(string key) => Path.Combine(fixture.AttachmentStorageDirectory, "avatars", key);

    private async Task UploadAsync(HttpClient client, string route, byte[] bytes)
    {
        using var form = Form(bytes, "image/png");
        using var response = await client.PutAsync(route, form, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static MultipartFormDataContent Form(byte[] bytes, string mime)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "File", "../../untrusted.png");
        return form;
    }

    private sealed class FailedSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated avatar save failure.");
    }

    private sealed class CancelledSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);
    }
}

public sealed class ProfileAvatarTests(PeopleApiFixture fixture) : ProfileAvatarTestsContract<PeopleApiFixture>(fixture);

[Collection("SQLite")]
public sealed class SqliteProfileAvatarTests(SqliteApiFixture fixture) : ProfileAvatarTestsContract<SqliteApiFixture>(fixture);
