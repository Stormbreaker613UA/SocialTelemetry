using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileAvatars;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forged_avatar_holder_uses_only_owned_identity_for_reads_and_real_writes(bool person)
    {
        var foreignProfile = await fixture.CreateUserProfileAsync();
        Guid? foreignPerson = person ? await fixture.CreatePersonAsync(new() { UserProfileId = foreignProfile, DisplayName = "Foreign" }) : null;
        var foreignRoute = person ? $"/people/{foreignPerson}/avatar" : "/user-profile/avatar";
        byte[] foreignBytes = [137, 80, 78, 71, 13, 10, 26, 10, 21];
        await UploadAvatarAsync(foreignRoute, foreignBytes);
        var owner = await fixture.CreateUserProfileAsync();
        Guid? ownPerson = person ? await fixture.CreatePersonAsync(new() { UserProfileId = owner, DisplayName = "Owned" }) : null;
        var ownRoute = person ? $"/people/{ownPerson}/avatar" : "/user-profile/avatar";
        byte[] ownBytes = [137, 80, 78, 71, 13, 10, 26, 10, 22];
        await UploadAvatarAsync(ownRoute, ownBytes);
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var foreignKey = person
            ? await database.People.AsNoTracking().Where(target => target.Id == foreignPerson).Select(target => target.AvatarStorageKey).SingleAsync(Cancellation)
            : await database.UserProfiles.AsNoTracking().Where(target => target.Id == foreignProfile).Select(target => target.AvatarStorageKey).SingleAsync(Cancellation);
        var forged = Holder(owner, ownPerson, foreignKey);
        var avatars = scope.ServiceProvider.GetRequiredService<ProfileAvatarService>();
        await AssertAvatarAsync(avatars, forged, ownBytes);
        // An untracked holder still writes the actual owned row; its supplied key is never used.
        byte[] replacement = [137, 80, 78, 71, 13, 10, 26, 10, 23];
        await avatars.UploadAsync(forged, replacement, "image/png", Cancellation);
        Assert.Equal(foreignKey, forged.StorageKey);
        var storedKey = person
            ? await database.People.AsNoTracking().Where(target => target.Id == ownPerson).Select(target => target.AvatarStorageKey).SingleAsync(Cancellation)
            : await database.UserProfiles.AsNoTracking().Where(target => target.Id == owner).Select(target => target.AvatarStorageKey).SingleAsync(Cancellation);
        Assert.NotNull(storedKey);
        Assert.NotEqual(foreignKey, storedKey);
        await AssertAvatarAsync(avatars, forged, replacement);
        await avatars.DeleteAsync(forged, Cancellation);
        var deletedKey = person
            ? await database.People.AsNoTracking().Where(target => target.Id == ownPerson).Select(target => target.AvatarStorageKey).SingleAsync(Cancellation)
            : await database.UserProfiles.AsNoTracking().Where(target => target.Id == owner).Select(target => target.AvatarStorageKey).SingleAsync(Cancellation);
        Assert.Null(deletedKey);
        await Assert.ThrowsAsync<NotFoundException>(() => avatars.OpenAsync(forged, Cancellation));
        var remainingKey = person
            ? await database.People.AsNoTracking().Where(target => target.Id == foreignPerson).Select(target => target.AvatarStorageKey).SingleAsync(Cancellation)
            : await database.UserProfiles.AsNoTracking().Where(target => target.Id == foreignProfile).Select(target => target.AvatarStorageKey).SingleAsync(Cancellation);
        Assert.Equal(foreignKey, remainingKey);
        await using var foreignContent = await scope.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars").OpenReadAsync(foreignKey!, Cancellation);
        Assert.NotNull(foreignContent);
        using var buffer = new MemoryStream();
        await foreignContent.CopyToAsync(buffer, Cancellation);
        Assert.Equal(foreignBytes, buffer.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Avatar_reads_ignore_tampered_tracking_and_old_snapshots_but_stale_writes_conflict(bool person)
    {
        var owner = await fixture.CreateUserProfileAsync();
        Guid? personId = person ? await fixture.CreatePersonAsync(new() { UserProfileId = owner, DisplayName = "Owned" }) : null;
        var route = person ? $"/people/{personId}/avatar" : "/user-profile/avatar";
        byte[] original = [137, 80, 78, 71, 13, 10, 26, 10, 31];
        byte[] replacement = [137, 80, 78, 71, 13, 10, 26, 10, 32];
        await UploadAvatarAsync(route, original);
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var avatars = scope.ServiceProvider.GetRequiredService<ProfileAvatarService>();
        var tracked = await avatars.FindOwnerAsync(personId, Cancellation);
        var snapshot = await avatars.FindOwnerAsync(personId, Cancellation, readOnly: true);
        var key = tracked.StorageKey;
        tracked.SetAvatar(Guid.NewGuid().ToString("N"), "image/jpeg");
        snapshot.SetAvatar(Guid.NewGuid().ToString("N"), "image/webp");
        await AssertAvatarAsync(avatars, tracked, original);
        await AssertAvatarAsync(avatars, snapshot, original);
        await Assert.ThrowsAsync<ConflictException>(() => avatars.UploadAsync(tracked, replacement, "image/png", Cancellation));
        await Assert.ThrowsAsync<ConflictException>(() => avatars.DeleteAsync(tracked, Cancellation));
        Assert.NotEqual(key, tracked.StorageKey); // Rejection must not discard the caller's pending edits.
        await UploadAvatarAsync(route, replacement);
        await AssertAvatarAsync(avatars, tracked, replacement);
        await AssertAvatarAsync(avatars, snapshot, replacement);
        await Assert.ThrowsAsync<ConflictException>(() => avatars.UploadAsync(snapshot, original, "image/png", Cancellation));
        await Assert.ThrowsAsync<ConflictException>(() => avatars.DeleteAsync(snapshot, Cancellation));
        using var download = await fixture.Client.GetAsync(route, Cancellation);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(replacement, await download.Content.ReadAsByteArrayAsync(Cancellation));
    }

    [Fact]
    public async Task Foreign_or_missing_avatar_targets_cannot_save_pending_work_or_touch_storage()
    {
        var foreign = await fixture.CreateUserProfileAsync();
        var foreignPerson = await fixture.CreatePersonAsync(new() { UserProfileId = foreign, DisplayName = "Foreign" });
        var owner = await fixture.CreateUserProfileAsync();
        var ownPerson = await fixture.CreatePersonAsync(new() { UserProfileId = owner, DisplayName = "Original" });
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await database.People.SingleAsync(target => target.Id == ownPerson, Cancellation);
        pending.DisplayName = "Unsaved";
        var avatars = scope.ServiceProvider.GetRequiredService<ProfileAvatarService>();
        var storage = scope.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars");
        var files = storage.EnumerateFiles().Select(file => file.StorageKey).Order().ToArray();
        var staged = storage.EnumerateStagedFiles().Select(file => file.StorageKey).Order().ToArray();
        foreach (var target in new[] { Holder(owner, foreignPerson, null), Holder(owner, Guid.NewGuid(), null), Holder(foreign, null, null), Holder(Guid.NewGuid(), null, null) })
        {
            await Assert.ThrowsAsync<NotFoundException>(() => avatars.OpenAsync(target, Cancellation));
            await Assert.ThrowsAsync<NotFoundException>(() => avatars.UploadAsync(target, [137, 80, 78, 71, 13, 10, 26, 10], "image/png", Cancellation));
            await Assert.ThrowsAsync<NotFoundException>(() => avatars.DeleteAsync(target, Cancellation));
        }
        Assert.Equal("Unsaved", pending.DisplayName);
        Assert.Equal(EntityState.Modified, database.Entry(pending).State);
        Assert.Equal("Original", await database.People.AsNoTracking().Where(target => target.Id == ownPerson).Select(target => target.DisplayName).SingleAsync(Cancellation));
        Assert.Equal(files, storage.EnumerateFiles().Select(file => file.StorageKey).Order().ToArray());
        Assert.Equal(staged, storage.EnumerateStagedFiles().Select(file => file.StorageKey).Order().ToArray());
    }

    [Fact]
    public async Task Avatar_holder_and_public_owner_binding_do_not_replace_real_admission()
    {
        var owner = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var selection = await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        scope.ServiceProvider.GetRequiredService<LocalProfileOperation>().Bind(new(owner, selection.Version), CancellationToken.None);
        var avatars = scope.ServiceProvider.GetRequiredService<ProfileAvatarService>();
        var target = Holder(owner, null, Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<LocalAccessException>(() => avatars.OpenAsync(target, Cancellation));
        await Assert.ThrowsAsync<LocalAccessException>(() => avatars.UploadAsync(target, [137, 80, 78, 71, 13, 10, 26, 10], "image/png", Cancellation));
        await Assert.ThrowsAsync<LocalAccessException>(() => avatars.DeleteAsync(target, Cancellation));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("person-avatar")]
    [InlineData("person-external-person")]
    [InlineData("person-external-link")]
    [InlineData("person-external-stub")]
    [InlineData("profile-external")]
    [InlineData("analysis")]
    [InlineData("message")]
    [InlineData("suggestion-person")]
    [InlineData("suggestion-analysis")]
    public async Task Journal_metadata_save_rechecks_durable_parents_after_owned_lookup(string mutation)
    {
        var owner = await fixture.CreateUserProfileAsync();
        var foreign = await fixture.CreateUserProfileAsync(select: false);
        var personId = Guid.NewGuid(); var foreignPerson = Guid.NewGuid();
        var interactionId = Guid.NewGuid(); var foreignInteraction = Guid.NewGuid();
        var analysisId = Guid.NewGuid(); var foreignAnalysis = Guid.NewGuid();
        var messageId = Guid.NewGuid(); var suggestionId = Guid.NewGuid();
        var personConnectionId = Guid.NewGuid(); var profileConnectionId = Guid.NewGuid();
        string key;
        await using (var seed = fixture.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var storage = seed.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars");
            using var bytes = new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10]);
            key = await storage.SaveAsync(bytes, Cancellation);
            Assert.True(await storage.CompleteUploadAsync(key, Cancellation));
            database.People.AddRange(new Person { Id = personId, UserProfileId = owner, DisplayName = "Owned", AvatarStorageKey = key, AvatarMimeType = "image/png" },
                new Person { Id = foreignPerson, UserProfileId = foreign, DisplayName = "Foreign" });
            database.Interactions.AddRange(new Interaction { Id = interactionId, UserProfileId = owner }, new Interaction { Id = foreignInteraction, UserProfileId = foreign });
            database.InteractionAnalyses.AddRange(new InteractionAnalysis { Id = analysisId, InteractionId = interactionId, Summary = "Original" },
                new InteractionAnalysis { Id = foreignAnalysis, InteractionId = foreignInteraction, Summary = "Foreign" });
            database.AnalysisConversationMessages.Add(new() { Id = messageId, InteractionAnalysisId = analysisId, Role = "user", Content = "Original" });
            database.SuggestedProfileUpdates.Add(new() { Id = suggestionId, InteractionAnalysisId = analysisId, PersonId = personId, Field = "Notes", SuggestedValue = "Original" });
            database.PersonExternalConnections.Add(new() { Id = personConnectionId, PersonId = personId, Platform = "Discord", DisplayName = "Original" });
            database.UserProfileExternalConnections.Add(new() { Id = profileConnectionId, UserProfileId = owner, Platform = "Discord", DisplayName = "Original" });
            await database.SaveChangesAsync(Cancellation);
        }
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider);
        using var admission = await scope.ServiceProvider.GetRequiredService<LocalProfileOperation>().EnterAsync(Cancellation, userMutation: true);
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry pending;
        if (mutation is "archive" or "person-avatar")
        {
            var person = await scope.ServiceProvider.GetRequiredService<AppFeatures.People.IPeoplePersistence>()
                .FindPersonAsync(owner, personId, readOnly: false, Cancellation);
            Assert.NotNull(person);
            if (mutation == "archive") person.ArchivedAt = DateTimeOffset.UtcNow;
            else person.AvatarMimeType = "image/jpeg";
            pending = context.Entry(person);
        }
        else if (mutation.StartsWith("person-external", StringComparison.Ordinal))
        {
            var connection = await context.PersonExternalConnections
                .SingleAsync(row => row.Id == personConnectionId && context.People.Any(person => person.Id == row.PersonId && person.UserProfileId == owner), Cancellation);
            if (mutation == "person-external-stub")
            {
                context.Entry(connection).State = EntityState.Detached;
                connection = new PersonExternalConnection { Id = personConnectionId, DisplayName = "Unsaved" };
                context.PersonExternalConnections.Attach(connection);
                context.Entry(connection).Property(row => row.DisplayName).IsModified = true;
            }
            else connection.DisplayName = "Unsaved";
            pending = context.Entry(connection);
        }
        else if (mutation == "profile-external")
        {
            var connection = await context.UserProfileExternalConnections.SingleAsync(row => row.Id == profileConnectionId && row.UserProfileId == owner, Cancellation);
            connection.DisplayName = "Unsaved"; pending = context.Entry(connection);
        }
        else if (mutation == "analysis")
        {
            var analysis = await context.InteractionAnalyses.SingleAsync(row => row.Id == analysisId && row.Interaction.UserProfileId == owner, Cancellation);
            analysis.Summary = "Unsaved"; pending = context.Entry(analysis);
        }
        else if (mutation == "message")
        {
            var message = await context.AnalysisConversationMessages.SingleAsync(row => row.Id == messageId && row.InteractionAnalysis.Interaction.UserProfileId == owner, Cancellation);
            message.Content = "Unsaved"; pending = context.Entry(message);
        }
        else
        {
            var suggestion = await context.SuggestedProfileUpdates.SingleAsync(row => row.Id == suggestionId && row.Person.UserProfileId == owner && row.InteractionAnalysis.Interaction.UserProfileId == owner, Cancellation);
            suggestion.SuggestedValue = "Unsaved"; pending = context.Entry(suggestion);
        }
        await using (var moving = fixture.CreateAsyncScope())
        {
            var database = moving.ServiceProvider.GetRequiredService<AppDbContext>();
            // A fixture-only legacy persistence writer commits after the owned lookup. No product reparent API.
            if (mutation is "archive" or "person-avatar" or "person-external-person")
                await database.People.Where(row => row.Id == personId).ExecuteUpdateAsync(update => update.SetProperty(row => row.UserProfileId, foreign), Cancellation);
            else if (mutation is "person-external-link" or "person-external-stub")
                await database.PersonExternalConnections.Where(row => row.Id == personConnectionId).ExecuteUpdateAsync(update => update.SetProperty(row => row.PersonId, foreignPerson), Cancellation);
            else if (mutation == "profile-external")
                await database.UserProfileExternalConnections.Where(row => row.Id == profileConnectionId).ExecuteUpdateAsync(update => update.SetProperty(row => row.UserProfileId, foreign), Cancellation);
            else if (mutation == "analysis")
                await database.InteractionAnalyses.Where(row => row.Id == analysisId).ExecuteUpdateAsync(update => update.SetProperty(row => row.InteractionId, foreignInteraction), Cancellation);
            else if (mutation == "message")
                await database.AnalysisConversationMessages.Where(row => row.Id == messageId).ExecuteUpdateAsync(update => update.SetProperty(row => row.InteractionAnalysisId, foreignAnalysis), Cancellation);
            else if (mutation == "suggestion-person")
                await database.SuggestedProfileUpdates.Where(row => row.Id == suggestionId).ExecuteUpdateAsync(update => update.SetProperty(row => row.PersonId, foreignPerson), Cancellation);
            else
                await database.SuggestedProfileUpdates.Where(row => row.Id == suggestionId).ExecuteUpdateAsync(update => update.SetProperty(row => row.InteractionAnalysisId, foreignAnalysis), Cancellation);
        }
        await Assert.ThrowsAsync<NotFoundException>(() => context.SaveChangesAsync(Cancellation));
        Assert.Equal(EntityState.Modified, pending.State);
        Assert.Equal(owner, scope.ServiceProvider.GetRequiredService<LocalProfileOperation>().RequireOwner());
        await using var checking = fixture.CreateAsyncScope();
        var check = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await check.People.Where(row => row.Id == personId).Select(row => row.ArchivedAt).SingleAsync(Cancellation));
        Assert.Equal("image/png", await check.People.Where(row => row.Id == personId).Select(row => row.AvatarMimeType).SingleAsync(Cancellation));
        Assert.Equal("Original", await check.PersonExternalConnections.Where(row => row.Id == personConnectionId).Select(row => row.DisplayName).SingleAsync(Cancellation));
        Assert.Equal("Original", await check.UserProfileExternalConnections.Where(row => row.Id == profileConnectionId).Select(row => row.DisplayName).SingleAsync(Cancellation));
        Assert.Equal("Original", await check.InteractionAnalyses.Where(row => row.Id == analysisId).Select(row => row.Summary).SingleAsync(Cancellation));
        Assert.Equal("Original", await check.AnalysisConversationMessages.Where(row => row.Id == messageId).Select(row => row.Content).SingleAsync(Cancellation));
        Assert.Equal("Original", await check.SuggestedProfileUpdates.Where(row => row.Id == suggestionId).Select(row => row.SuggestedValue).SingleAsync(Cancellation));
        Assert.Equal("Foreign", await check.InteractionAnalyses.Where(row => row.Id == foreignAnalysis).Select(row => row.Summary).SingleAsync(Cancellation));
        Assert.True(await checking.ServiceProvider.GetRequiredKeyedService<IFileStorage>("avatars").ExistsAsync(key, Cancellation));
    }

    private static AvatarOwner Holder(Guid owner, Guid? person, string? key) => person is Guid personId
        ? new AvatarOwner(new SocialTelemetry.Domain.People.Person { Id = personId, UserProfileId = owner, AvatarStorageKey = key, AvatarMimeType = "image/png" })
        : new AvatarOwner(new SocialTelemetry.Domain.Users.UserProfile { Id = owner, AvatarStorageKey = key, AvatarMimeType = "image/png" });

    private async Task UploadAvatarAsync(string route, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "File", "synthetic.png");
        using var response = await fixture.Client.PutAsync(route, form, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task AssertAvatarAsync(ProfileAvatarService avatars, AvatarOwner target, byte[] expected)
    {
        var avatar = await avatars.OpenAsync(target, Cancellation);
        await using var content = avatar.Content;
        Assert.Equal("image/png", avatar.MimeType);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, Cancellation);
        Assert.Equal(expected, buffer.ToArray());
    }

    private async Task BindAsync(IServiceProvider services)
    {
        var selector = services.GetRequiredService<LocalProfileSelectionService>();
        var selection = await selector.GetAsync(Cancellation);
        var ownerId = Assert.IsType<Guid>(selection.UserProfileId);
        await services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
            .ExecuteAsync(ownerId, selection.Version, Cancellation);
    }
}

public sealed class JournalApplicationTests(PeopleApiFixture fixture) : JournalApplicationTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteJournalApplicationTests(SqliteApiFixture fixture) : JournalApplicationTestsContract<SqliteApiFixture>(fixture);
