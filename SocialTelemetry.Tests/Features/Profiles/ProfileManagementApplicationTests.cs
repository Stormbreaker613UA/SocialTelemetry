using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.ProfileAccess;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Tests.Features.People;
using SocialTelemetry.Tests.Infrastructure.AI;
using AppFeatures = SocialTelemetry.Application.Features;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class ProfileManagementApplicationTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Direct_create_validates_before_CAS_and_preserves_selected_owner_and_lifetime()
    {
        var owner = await fixture.CreateUserProfileAsync();
        await using var scope = fixture.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var list = await services.GetRequiredService<AppFeatures.LocalProfiles.Get.Scenario>().ExecuteAsync(Cancellation);
        await using var journal = fixture.CreateAsyncScope();
        var bound = await ResolveAsync(journal.ServiceProvider, list.Selection.UserProfileId, list.Selection.Version);
        var create = services.GetRequiredService<AppFeatures.LocalProfiles.Create.Scenario>();
        await Assert.ThrowsAsync<InvalidInputException>(() => create.ExecuteAsync(" ", Guid.Empty, Cancellation));
        await Assert.ThrowsAsync<InvalidInputException>(() => create.ExecuteAsync(new string('x', 201), Guid.Empty, Cancellation));
        var stale = await Assert.ThrowsAsync<LocalProfileAccessException>(() => create.ExecuteAsync("Valid", Guid.Empty, Cancellation));
        Assert.Equal(LocalProfileFailure.SelectionChanged, stale.Failure);
        var profile = await create.ExecuteAsync("  Created locally  ", list.Selection.Version, Cancellation);
        Assert.Equal("Created locally", profile.DisplayName);
        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.False(bound.Lifetime.IsCancellationRequested);
        var after = await services.GetRequiredService<AppFeatures.LocalProfiles.Get.Scenario>().ExecuteAsync(Cancellation);
        Assert.Equal(owner, after.Selection.UserProfileId);
        Assert.Equal(list.Selection.Version, after.Selection.Version);
        Assert.False(after.Selection.LegacySingleProfileAccess);
        Assert.False(Assert.Single(after.Profiles, identity => identity.Id == profile.Id).AccountLinked);
        Assert.False(await services.GetRequiredService<AppDbContext>().Users.AnyAsync(account => account.UserProfileId == profile.Id, Cancellation));
    }

    [Fact]
    public async Task Direct_access_binds_once_and_authorizes_both_root_kinds_without_retargeting()
    {
        var foreign = await fixture.CreateUserProfileAsync();
        var owner = await fixture.CreateUserProfileAsync();
        var ownPerson = new Person { Id = Guid.NewGuid(), UserProfileId = owner, DisplayName = "Owned" };
        var foreignPerson = new Person { Id = Guid.NewGuid(), UserProfileId = foreign, DisplayName = "Foreign" };
        var ownInteraction = new Interaction { Id = Guid.NewGuid(), UserProfileId = owner, Title = "Owned" };
        var foreignInteraction = new Interaction { Id = Guid.NewGuid(), UserProfileId = foreign, Title = "Foreign" };
        await using (var seed = fixture.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            database.AddRange(ownPerson, foreignPerson, ownInteraction, foreignInteraction);
            await database.SaveChangesAsync(Cancellation);
        }
        await using var scope = fixture.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var selection = (await services.GetRequiredService<AppFeatures.LocalProfiles.Get.Scenario>().ExecuteAsync(Cancellation)).Selection;
        var forged = await Assert.ThrowsAsync<LocalProfileAccessException>(() => ResolveAsync(services, foreign, selection.Version));
        Assert.Equal(LocalProfileFailure.SelectionChanged, forged.Failure);
        Assert.Null(services.GetRequiredService<LocalProfileOperation>().Binding);
        var resolved = await ResolveAsync(services, owner, selection.Version);
        var authorize = services.GetRequiredService<AppFeatures.ProfileAccess.AuthorizeRoot.Scenario>();
        await authorize.ExecuteAsync(OwnershipRoot.Person, ownPerson.Id, Cancellation);
        await authorize.ExecuteAsync(OwnershipRoot.Interaction, ownInteraction.Id, Cancellation);
        await Assert.ThrowsAsync<NotFoundException>(() => authorize.ExecuteAsync(OwnershipRoot.Person, foreignPerson.Id, Cancellation));
        await Assert.ThrowsAsync<NotFoundException>(() => authorize.ExecuteAsync(OwnershipRoot.Interaction, foreignInteraction.Id, Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync(services, owner, selection.Version));
        await fixture.SelectProfileAsync(foreign);
        Assert.True(resolved.Lifetime.IsCancellationRequested);
        Assert.Equal(owner, services.GetRequiredService<LocalProfileOperation>().RequireOwner());
    }

    [Fact]
    public async Task Direct_select_and_resolve_fail_closed_for_linked_accounts()
    {
        await fixture.CreateUserProfileAsync();
        var protectedProfile = await fixture.CreateUserProfileAsync(select: false);
        await using var scope = fixture.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<AppDbContext>();
        database.Users.Add(new ApplicationUser { UserProfileId = protectedProfile, UserName = "synthetic-local-account" });
        await database.SaveChangesAsync(Cancellation);
        var list = await services.GetRequiredService<AppFeatures.LocalProfiles.Get.Scenario>().ExecuteAsync(Cancellation);
        Assert.True(Assert.Single(list.Profiles, identity => identity.Id == protectedProfile).AccountLinked);
        var select = services.GetRequiredService<AppFeatures.LocalProfiles.Select.Scenario>();
        var rejected = await Assert.ThrowsAsync<LocalProfileAccessException>(() => select.ExecuteAsync(protectedProfile, list.Selection.Version, Cancellation));
        Assert.Equal(LocalProfileFailure.ProtectedAccessUnavailable, rejected.Failure);
        Assert.Equal(list.Selection, (await services.GetRequiredService<AppFeatures.LocalProfiles.Get.Scenario>().ExecuteAsync(Cancellation)).Selection);
        // Linking an account to the selected identity must also close direct access, without a selector/UI bypass.
        database.Users.Add(new ApplicationUser { UserProfileId = Assert.IsType<Guid>(list.Selection.UserProfileId), UserName = "synthetic-active-account" });
        await database.SaveChangesAsync(Cancellation);
        var locked = await Assert.ThrowsAsync<LocalProfileAccessException>(() => ResolveAsync(services, list.Selection.UserProfileId, list.Selection.Version));
        Assert.Equal(LocalProfileFailure.ProtectedAccessUnavailable, locked.Failure);
        Assert.Null(services.GetRequiredService<LocalProfileOperation>().Binding);
    }

    [Fact]
    public async Task Direct_AI_management_requires_explicit_claim_and_preserves_profile_model_generations()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        var browser = new TestBrowser();
        using var server = new FakeChatGptServer(browser)
        {
            ModelsBody = """{"models":[{"slug":"model-a","display_name":"First","visibility":"list"},{"slug":"model-b","display_name":"Second","visibility":"list"}]}"""
        };
        using var application = fixture.WithServices(services => ConfigureFake(services, browser, server));
        try
        {
            var store = application.Services.GetRequiredService<ChatGptCredentialStore>();
            var account = new ChatGptAccount { ClientId = "synthetic-client", SelectedModel = "model-a", Tokens = new()
            {
                AccessToken = "private-access-synthetic", SessionId = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Scopes = [ChatGptProtocol.DirectScope, "resource.invoke"]
            } };
            using (await store.LockAsync(Cancellation))
            {
                var state = await store.ReadAsync(Cancellation);
                state.Accounts.Add(account);
                state.ActiveConnectionId = account.Id; // This legacy global field must never authorize the selected profile.
                await store.SaveAsync(state, Cancellation);
            }
            await using var scope = application.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var selection = (await services.GetRequiredService<AppFeatures.LocalProfiles.Get.Scenario>().ExecuteAsync(Cancellation)).Selection;
            await ResolveAsync(services, owner, selection.Version);
            var status = services.GetRequiredService<AppFeatures.AiConnection.Status.Scenario>();
            Assert.False((await status.ExecuteAsync(Cancellation)).Connected);
            Assert.Equal(account.Id, Assert.Single(await services.GetRequiredService<AppFeatures.AiConnection.Unassigned.Scenario>().ExecuteAsync(Cancellation)).Id);
            var select = services.GetRequiredService<AppFeatures.AiConnection.SelectConnection.Scenario>();
            var denied = await Assert.ThrowsAsync<AiProviderException>(() => select.ExecuteAsync(account.Id, false, Cancellation));
            Assert.Equal(AiFailure.NotConnected, denied.Failure);
            await select.ExecuteAsync(account.Id, true, Cancellation);
            Assert.Equal(account.Id, (await status.ExecuteAsync(Cancellation)).ConnectionId);
            var catalog = await services.GetRequiredService<AppFeatures.AiConnection.Models.Scenario>().ExecuteAsync(Cancellation);
            Assert.Equal("model-a", catalog.SelectedModel);
            Assert.True(catalog.SelectedModelAvailable);
            Assert.Equal(new[] { "model-a", "model-b" }, catalog.Models.Select(model => model.Id));
            var access = services.GetRequiredService<ProfileAiAccess>();
            var original = await access.CaptureAsync(Cancellation);
            var model = services.GetRequiredService<AppFeatures.AiConnection.SelectModel.Scenario>();
            var invalid = await Assert.ThrowsAsync<AiProviderException>(() => model.ExecuteAsync(" ", Cancellation));
            Assert.Equal(AiFailure.ModelUnavailable, invalid.Failure);
            var unavailable = await Assert.ThrowsAsync<AiProviderException>(() => model.ExecuteAsync("missing", Cancellation));
            Assert.Equal(AiFailure.ModelUnavailable, unavailable.Failure);
            await model.ExecuteAsync("model-a", Cancellation);
            Assert.False(original.Lifetime.IsCancellationRequested);
            Assert.Equal(original.Binding, (await access.CaptureAsync(Cancellation)).Binding);
            await model.ExecuteAsync("model-b", Cancellation);
            Assert.True(original.Lifetime.IsCancellationRequested);
            var current = await access.CaptureAsync(Cancellation);
            Assert.Equal("model-b", current.Model);
            Assert.False(current.Lifetime.IsCancellationRequested);
            await using (var switching = application.Services.CreateAsyncScope())
                await switching.ServiceProvider.GetRequiredService<AppFeatures.LocalProfiles.Select.Scenario>().ExecuteAsync(other, selection.Version, Cancellation);
            await using var next = application.Services.CreateAsyncScope();
            var nextSelection = (await next.ServiceProvider.GetRequiredService<AppFeatures.LocalProfiles.Get.Scenario>().ExecuteAsync(Cancellation)).Selection;
            await ResolveAsync(next.ServiceProvider, other, nextSelection.Version);
            Assert.False((await next.ServiceProvider.GetRequiredService<AppFeatures.AiConnection.Status.Scenario>().ExecuteAsync(Cancellation)).Connected);
            var foreign = await Assert.ThrowsAsync<AiProviderException>(() => next.ServiceProvider.GetRequiredService<AppFeatures.AiConnection.SelectConnection.Scenario>()
                .ExecuteAsync(account.Id, true, Cancellation));
            Assert.Equal(AiFailure.NotConnected, foreign.Failure);
            Assert.Empty(await next.ServiceProvider.GetRequiredService<AppFeatures.AiConnection.Unassigned.Scenario>().ExecuteAsync(Cancellation));
        }
        finally { server.DisposeKey(); }
    }

    private void ConfigureFake(IServiceCollection services, TestBrowser browser, FakeChatGptServer server)
    {
        var directory = Path.GetFullPath(Path.Combine(fixture.AttachmentStorageDirectory, "..", "direct-ai-" + Guid.NewGuid()));
        services.Configure<ChatGptOptions>(options => options.DataDirectory = directory);
        services.RemoveAll<ISystemBrowser>();
        services.AddSingleton<ISystemBrowser>(browser);
        services.AddHttpClient<ChatGptHttpClient>().ConfigurePrimaryHttpMessageHandler(() => server).RemoveAllLoggers();
    }

    private static Task<ResolvedProfile> ResolveAsync(IServiceProvider services, Guid? owner, Guid? version) =>
        services.GetRequiredService<AppFeatures.ProfileAccess.Resolve.Scenario>().ExecuteAsync(owner, version, Cancellation);
}

public sealed class ProfileManagementApplicationTests(PeopleApiFixture fixture) : ProfileManagementApplicationTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteProfileManagementApplicationTests(SqliteApiFixture fixture) : ProfileManagementApplicationTestsContract<SqliteApiFixture>(fixture);
