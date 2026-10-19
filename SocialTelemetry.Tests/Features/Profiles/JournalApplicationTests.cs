using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileAvatars;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Tests.Features.People;
using AppFeatures = SocialTelemetry.Application.Features;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class JournalApplicationTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Direct_scenarios_validate_before_resource_lookup_and_preserve_messages()
    {
        await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var create = scope.ServiceProvider.GetRequiredService<AppFeatures.People.Create.Scenario>();
        var invalidCreate = await Assert.ThrowsAsync<FeatureValidationException>(() => create.ExecuteAsync(
            new() { UserProfileId = Guid.NewGuid(), DisplayName = " " }, Cancellation));
        Assert.Equal("UserProfileId and DisplayName are required.", invalidCreate.Message);
        var update = scope.ServiceProvider.GetRequiredService<AppFeatures.People.Update.Scenario>();
        var invalidUpdate = await Assert.ThrowsAsync<FeatureValidationException>(() => update.ExecuteAsync(
            new() { Id = Guid.NewGuid(), DisplayName = new string('a', 201) }, Cancellation));
        Assert.Equal("DisplayName cannot exceed 200 characters and RelationshipContext must be supported.", invalidUpdate.Message);
        var fact = scope.ServiceProvider.GetRequiredService<AppFeatures.PersonFacts.Add.Scenario>();
        var invalidFact = await Assert.ThrowsAsync<FeatureValidationException>(() => fact.ExecuteAsync(
            new() { PersonId = Guid.NewGuid(), Value = " " }, Cancellation));
        Assert.Equal("Value is required.", invalidFact.Message);
        var journal = scope.ServiceProvider.GetRequiredService<AppFeatures.UserProfile.Update.Scenario>();
        var invalidJournal = await Assert.ThrowsAsync<FeatureValidationException>(() => journal.ExecuteAsync(
            new() { DisplayName = " " }, Cancellation));
        Assert.Equal("DisplayName is required.", invalidJournal.Message);
    }

    [Fact]
    public async Task Direct_scenarios_keep_foreign_parent_unavailable_before_connection_validation()
    {
        var firstProfile = await fixture.CreateUserProfileAsync();
        var foreignPerson = await fixture.CreatePersonAsync(new() { UserProfileId = firstProfile, DisplayName = "First" });
        var selectedProfile = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var people = scope.ServiceProvider.GetRequiredService<AppFeatures.People.GetById.Scenario>();
        await Assert.ThrowsAsync<NotFoundException>(() => people.ExecuteAsync(foreignPerson, Cancellation));
        var addConnection = scope.ServiceProvider.GetRequiredService<AppFeatures.People.AddExternalConnection.Scenario>();
        await Assert.ThrowsAsync<NotFoundException>(() => addConnection.ExecuteAsync(
            new() { PersonId = foreignPerson, Platform = "invalid platform" }, Cancellation));
        var facts = scope.ServiceProvider.GetRequiredService<AppFeatures.PersonFacts.Add.Scenario>();
        await Assert.ThrowsAsync<NotFoundException>(() => facts.ExecuteAsync(
            new() { PersonId = foreignPerson, Value = "Valid" }, Cancellation));
        var create = scope.ServiceProvider.GetRequiredService<AppFeatures.People.Create.Scenario>();
        await Assert.ThrowsAsync<NotFoundException>(() => create.ExecuteAsync(
            new() { UserProfileId = firstProfile, DisplayName = "Valid" }, Cancellation));
        var profile = await scope.ServiceProvider.GetRequiredService<AppFeatures.UserProfile.Get.Scenario>().ExecuteAsync(Cancellation);
        Assert.Equal(selectedProfile, profile.Id);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.PersonFacts.AnyAsync(fact => fact.PersonId == foreignPerson, Cancellation));
        Assert.False(await database.PersonExternalConnections.AnyAsync(connection => connection.PersonId == foreignPerson, Cancellation));
    }

    [Fact]
    public async Task Direct_mutation_cannot_commit_after_selector_changes_and_does_not_retarget()
    {
        var originalProfile = await fixture.CreateUserProfileAsync();
        var personId = await fixture.CreatePersonAsync(new() { UserProfileId = originalProfile, DisplayName = "Original" });
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var update = scope.ServiceProvider.GetRequiredService<AppFeatures.People.Update.Scenario>();
        var nextProfile = await fixture.CreateUserProfileAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => update.ExecuteAsync(
            new() { Id = personId, DisplayName = "Late" }, Cancellation));
        Assert.True(scope.ServiceProvider.GetRequiredService<LocalProfileOperation>().Lifetime.IsCancellationRequested);
        Assert.Equal(originalProfile, scope.ServiceProvider.GetRequiredService<LocalProfileOperation>().RequireOwner());
        await using var verification = fixture.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Original", await database.People.Where(person => person.Id == personId)
            .Select(person => person.DisplayName).SingleAsync(Cancellation));
        Assert.False(await database.People.AnyAsync(person => person.UserProfileId == nextProfile, Cancellation));
    }

    [Fact]
    public async Task Direct_avatar_validation_and_foreign_owner_leave_storage_unchanged()
    {
        var firstProfile = await fixture.CreateUserProfileAsync();
        var foreignPerson = await fixture.CreatePersonAsync(new() { UserProfileId = firstProfile, DisplayName = "First" });
        var profileId = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var avatars = scope.ServiceProvider.GetRequiredService<ProfileAvatarService>();
        await Assert.ThrowsAsync<NotFoundException>(() => avatars.FindOwnerAsync(foreignPerson, Cancellation));
        var owner = await avatars.FindOwnerAsync(null, Cancellation);
        var storage = scope.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars");
        var originalFiles = storage.EnumerateFiles().Select(file => file.StorageKey).Order().ToArray();
        var originalStaged = storage.EnumerateStagedFiles().Select(file => file.StorageKey).Order().ToArray();
        await Assert.ThrowsAsync<FeatureValidationException>(() => avatars.UploadAsync(owner, [], "image/png", Cancellation));
        await Assert.ThrowsAsync<FeatureValidationException>(() => avatars.UploadAsync(owner, [1, 2, 3], "image/png", Cancellation));
        var limit = scope.ServiceProvider.GetRequiredService<IOptions<UploadOptions>>().Value.AvatarMaxBytes;
        await Assert.ThrowsAsync<FeatureValidationException>(() => avatars.UploadAsync(
            owner, new byte[checked((int)limit + 1)], "image/png", Cancellation));
        await Assert.ThrowsAsync<NotFoundException>(() => avatars.UploadAsync(
            new AvatarOwner(new SocialTelemetry.Domain.Users.UserProfile { Id = firstProfile }),
            [137, 80, 78, 71, 13, 10, 26, 10], "image/png", Cancellation));
        Assert.Equal(originalFiles, storage.EnumerateFiles().Select(file => file.StorageKey).Order().ToArray());
        Assert.Equal(originalStaged, storage.EnumerateStagedFiles().Select(file => file.StorageKey).Order().ToArray());
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await database.UserProfiles.Where(profile => profile.Id == profileId)
            .Select(profile => profile.AvatarStorageKey).SingleAsync(Cancellation));
    }

    private async Task BindAsync(IServiceProvider services)
    {
        var selector = services.GetRequiredService<LocalProfileSelectionService>();
        var selection = await selector.GetAsync(Cancellation);
        var ownerId = Assert.IsType<Guid>(selection.UserProfileId);
        services.GetRequiredService<LocalProfileOperation>().Bind(new(ownerId, selection.Version),
            services.GetRequiredService<LocalProfileLifetimes>().Capture(selection.Version));
    }
}

public sealed class JournalApplicationTests(PeopleApiFixture fixture) : JournalApplicationTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteJournalApplicationTests(SqliteApiFixture fixture) : JournalApplicationTestsContract<SqliteApiFixture>(fixture);
