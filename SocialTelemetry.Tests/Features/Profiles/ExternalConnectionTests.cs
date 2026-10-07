using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Domain.Users;
using SocialTelemetry.Api.Features.ProfileConnections;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Features.Profiles;

public sealed class ExternalConnectionTests(PeopleApiFixture fixture) : IClassFixture<PeopleApiFixture>
{
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crud_normalizes_platform_and_preserves_stable_identity(bool person)
    {
        var (route, ownerId) = await OwnerAsync(person);
        var connection = await AddAsync(route, new { Platform = "  DiScOrD  ", ExternalUserId = " stable-123 ", Handle = " old " });
        Assert.Equal(ownerId, connection.OwnerId);
        Assert.Equal("discord", connection.Platform);
        Assert.Equal("stable-123", connection.ExternalUserId);
        Assert.Equal("old", connection.Handle);
        using var get = await fixture.Client.GetAsync($"{route}/{connection.Id}", Cancellation);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var read = await get.Content.ReadFromJsonAsync<ExternalConnectionResponse>(Cancellation);
        Assert.NotNull(read);
        Assert.Equal(connection with { CreatedAt = read.CreatedAt, UpdatedAt = read.UpdatedAt }, read);
        Assert.InRange((connection.CreatedAt - read.CreatedAt).Duration(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
        Assert.InRange((connection.UpdatedAt - read.UpdatedAt).Duration(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
        using var listResponse = await fixture.Client.GetAsync(route, Cancellation);
        var list = await listResponse.Content.ReadFromJsonAsync<List<ExternalConnectionResponse>>(Cancellation);
        Assert.NotNull(list);
        Assert.Single(list, item => item.Id == connection.Id);

        using var update = await fixture.Client.PutAsJsonAsync($"{route}/{connection.Id}", new
        {
            Platform = "DISCORD", ExternalUserId = "stable-123", Handle = "new-handle",
            DisplayName = "Visible name", ProfileUrl = "https://example.com/profile"
        }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<ExternalConnectionResponse>(Cancellation);
        Assert.NotNull(updated);
        Assert.Equal("new-handle", updated.Handle);
        Assert.Equal(read.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= read.UpdatedAt);
        using var delete = await fixture.Client.DeleteAsync($"{route}/{connection.Id}", Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var missing = await fixture.Client.GetAsync($"{route}/{connection.Id}", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_stable_identity_returns_safe_conflict_on_create_and_update(bool person)
    {
        var (route, _) = await OwnerAsync(person);
        await AddAsync(route, new { Platform = "steam", ExternalUserId = "stable" });
        using var duplicate = await fixture.Client.PostAsJsonAsync(route, new { Platform = " STEAM ", ExternalUserId = "stable" }, Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var error = await duplicate.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("IX_", error);
        Assert.DoesNotContain("stable", error);
        var other = await AddAsync(route, new { Platform = "steam", ExternalUserId = "other" });
        using var update = await fixture.Client.PutAsJsonAsync($"{route}/{other.Id}", new { Platform = "steam", ExternalUserId = "stable" }, Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        // Optional IDs do not turn a handle-only account into a globally unique identity.
        await AddAsync(route, new { Platform = "website", Handle = "one", ExternalUserId = " " });
        await AddAsync(route, new { Platform = "website", Handle = "two" });
    }

    [Theory]
    [InlineData(false, "platform")]
    [InlineData(true, "platform")]
    [InlineData(false, "null-platform")]
    [InlineData(true, "null-platform")]
    [InlineData(false, "id")]
    [InlineData(true, "handle")]
    [InlineData(false, "name")]
    [InlineData(true, "url-length")]
    [InlineData(false, "url-scheme")]
    [InlineData(true, "url-credentials")]
    public async Task Invalid_fields_are_rejected_for_create_and_update(bool person, string fault)
    {
        var (route, _) = await OwnerAsync(person);
        var existing = await AddAsync(route, new { Platform = "custom" });
        var input = new Dictionary<string, object?> { ["Platform"] = "custom" };
        switch (fault)
        {
            case "platform": input["Platform"] = "bad platform"; break;
            case "null-platform": input["Platform"] = null; break;
            case "id": input["ExternalUserId"] = new string('a', 129); break;
            case "handle": input["Handle"] = new string('a', 201); break;
            case "name": input["DisplayName"] = new string('a', 201); break;
            case "url-length": input["ProfileUrl"] = "https://example.com/" + new string('a', 2048); break;
            case "url-scheme": input["ProfileUrl"] = "file:///C:/private"; break;
            case "url-credentials": input["ProfileUrl"] = "https://user:secret@example.com/"; break;
        }
        using var create = await fixture.Client.PostAsJsonAsync(route, input, Cancellation);
        using var update = await fixture.Client.PutAsJsonAsync($"{route}/{existing.Id}", input, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Person_connection_cannot_be_accessed_through_another_owner_and_identity_can_repeat_across_owners()
    {
        var (route, _) = await OwnerAsync(true);
        var own = await AddAsync(route, new { Platform = "discord", ExternalUserId = "same" });
        var otherProfile = await fixture.CreateUserProfileAsync();
        var otherPerson = await fixture.CreatePersonAsync(new() { UserProfileId = otherProfile, DisplayName = "Other" });
        var otherRoute = $"/people/{otherPerson}/external-connections";
        await AddAsync(otherRoute, new { Platform = "discord", ExternalUserId = "same" });
        await AssertForeignAsync(otherRoute, own.Id);
        using var listResponse = await fixture.Client.GetAsync(otherRoute, Cancellation);
        var list = await listResponse.Content.ReadFromJsonAsync<List<ExternalConnectionResponse>>(Cancellation);
        Assert.NotNull(list);
        Assert.DoesNotContain(list, connection => connection.Id == own.Id);
    }

    [Fact]
    public async Task Current_profile_connection_routes_cannot_access_another_profiles_connections()
    {
        await OwnerAsync(false);
        await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var foreignProfile = await database.UserProfiles.OrderByDescending(profile => profile.Id).FirstAsync(Cancellation);
        var foreign = new UserProfileExternalConnection { Id = Guid.NewGuid(), UserProfileId = foreignProfile.Id, Platform = "custom" };
        database.UserProfileExternalConnections.Add(foreign);
        await database.SaveChangesAsync(Cancellation);
        await AssertForeignAsync("/user-profile/external-connections", foreign.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_delete_cascades_connections(bool person)
    {
        var (route, ownerId) = await OwnerAsync(person);
        var connection = await AddAsync(route, new { Platform = "custom" });
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (person) database.People.Remove(await database.People.SingleAsync(owner => owner.Id == ownerId, Cancellation));
        else database.UserProfiles.Remove(await database.UserProfiles.SingleAsync(owner => owner.Id == ownerId, Cancellation));
        await database.SaveChangesAsync(Cancellation);
        Assert.False(person
            ? await database.PersonExternalConnections.AnyAsync(item => item.Id == connection.Id, Cancellation)
            : await database.UserProfileExternalConnections.AnyAsync(item => item.Id == connection.Id, Cancellation));
    }

    [Fact]
    public async Task Missing_owner_and_connection_return_not_found()
    {
        await fixture.ClearUserProfilesAsync();
        foreach (var route in new[] { "/user-profile/external-connections", $"/people/{Guid.NewGuid()}/external-connections" })
        {
            using var list = await fixture.Client.GetAsync(route, Cancellation);
            using var add = await fixture.Client.PostAsJsonAsync(route, new { Platform = "custom" }, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, add.StatusCode);
        }
        var (existingRoute, _) = await OwnerAsync(true);
        await AssertForeignAsync(existingRoute, Guid.NewGuid());
    }

    private async Task AssertForeignAsync(string route, Guid id)
    {
        using var get = await fixture.Client.GetAsync($"{route}/{id}", Cancellation);
        using var delete = await fixture.Client.DeleteAsync($"{route}/{id}", Cancellation);
        using var update = await fixture.Client.PutAsJsonAsync($"{route}/{id}", new { Platform = "custom" }, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    private async Task<(string Route, Guid OwnerId)> OwnerAsync(bool person)
    {
        await fixture.ClearUserProfilesAsync();
        var profileId = await fixture.CreateUserProfileAsync();
        if (!person) return ("/user-profile/external-connections", profileId);
        var personId = await fixture.CreatePersonAsync(new() { UserProfileId = profileId, DisplayName = "Synthetic" });
        return ($"/people/{personId}/external-connections", personId);
    }

    private async Task<ExternalConnectionResponse> AddAsync(string route, object input)
    {
        using var response = await fixture.Client.PostAsJsonAsync(route, input, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExternalConnectionResponse>(Cancellation);
        Assert.NotNull(created);
        return created;
    }
}
