using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Npgsql;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.PersonGroups;
using SocialTelemetry.Domain.People;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using GroupCreate = SocialTelemetry.Application.Features.PersonGroups.Create.Scenario;
using Favorite = SocialTelemetry.Application.Features.People.SetFavorite.Scenario;
using AddMember = SocialTelemetry.Application.Features.PersonGroups.AddMember.Scenario;
using RemoveMember = SocialTelemetry.Application.Features.PersonGroups.RemoveMember.Scenario;
using DeleteGroup = SocialTelemetry.Application.Features.PersonGroups.Delete.Scenario;

namespace SocialTelemetry.Tests.Features.People;

public abstract class FavoritesGroupsTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task BindAsync(IServiceProvider services, Guid owner)
    {
        var selection = await services.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
        await services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario>()
            .ExecuteAsync(owner, selection.Version, Cancellation);
    }

    private async Task<(Guid Owner, Guid Person)> SeedAsync()
    {
        var owner = await fixture.CreateUserProfileAsync();
        var person = await fixture.CreatePersonAsync(new() { UserProfileId = owner, DisplayName = "Organized person", Notes = "Retained notes" });
        return (owner, person);
    }

    [Fact]
    public async Task Http_favorite_multigroups_full_update_archive_and_combined_filters_preserve_journal()
    {
        var data = await SeedAsync();
        using var created = await fixture.Client.PostAsJsonAsync("/person-groups", new { Name = "  Friends  " }, Cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var group = (await created.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.PersonGroups.GroupResponse>(Cancellation))!;
        Assert.Equal(data.Owner, group.UserProfileId); Assert.Equal("Friends", group.Name);
        using var duplicate = await fixture.Client.PostAsJsonAsync("/person-groups", new { Name = "Friends" }, Cancellation);
        var second = (await duplicate.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.PersonGroups.GroupResponse>(Cancellation))!;
        Assert.NotEqual(group.Id, second.Id);
        using var listed = await fixture.Client.GetAsync("/person-groups", Cancellation);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(2, (await listed.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.PersonGroups.List.Response>(Cancellation))!.Groups.Count);
        using var fetched = await fixture.Client.GetAsync($"/person-groups/{group.Id}", Cancellation);
        Assert.Equal(group, await fetched.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.PersonGroups.GroupResponse>(Cancellation));
        using var rename = await fixture.Client.PatchAsJsonAsync($"/person-groups/{group.Id}", new { Name = "  Renamed  " }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var renamed = (await rename.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.PersonGroups.GroupResponse>(Cancellation))!;
        Assert.Equal("Renamed", renamed.Name);
        using var noRename = await fixture.Client.PatchAsJsonAsync($"/person-groups/{group.Id}", new { Name = "Renamed" }, Cancellation);
        Assert.Equal(renamed, await noRename.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.PersonGroups.GroupResponse>(Cancellation));
        foreach (var groupId in new[] { group.Id, second.Id })
        {
            using var add = await fixture.Client.PutAsync($"/person-groups/{groupId}/members/{data.Person}", null, Cancellation);
            Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
            using var repeat = await fixture.Client.PutAsync($"/person-groups/{groupId}/members/{data.Person}", null, Cancellation);
            Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        }
        using var missing = await fixture.Client.PutAsJsonAsync($"/people/{data.Person}/favorite", new { }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var set = await fixture.Client.PutAsJsonAsync($"/people/{data.Person}/favorite", new { IsFavorite = true }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var favorite = (await set.Content.ReadFromJsonAsync<SocialTelemetry.Application.Features.People.SetFavorite.Result>(Cancellation))!;
        using var repeated = await fixture.Client.PutAsJsonAsync($"/people/{data.Person}/favorite", new { IsFavorite = true }, Cancellation);
        Assert.Equal(favorite, await repeated.Content.ReadFromJsonAsync<SocialTelemetry.Application.Features.People.SetFavorite.Result>(Cancellation));
        using var update = await fixture.Client.PutAsJsonAsync($"/people/{data.Person}", new { DisplayName = "Updated name", Notes = "Retained notes" }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var archive = await fixture.Client.PutAsync($"/people/{data.Person}/archive", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        using var people = await fixture.Client.GetAsync($"/people?groupId={group.Id}&isFavorite=true&archived=true", Cancellation);
        var result = await people.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.People.GetAll.Response>(Cancellation);
        Assert.True(Assert.Single(result!.People).IsFavorite);
        using var active = await fixture.Client.GetAsync($"/person-groups/{group.Id}/members?archived=false", Cancellation);
        Assert.Empty((await active.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.PersonGroups.ListMembers.Response>(Cancellation))!.Members);
        using var all = await fixture.Client.GetAsync($"/person-groups/{group.Id}/members", Cancellation);
        Assert.NotNull(Assert.Single((await all.Content.ReadFromJsonAsync<SocialTelemetry.Api.Features.PersonGroups.ListMembers.Response>(Cancellation))!.Members).ArchivedAt);
        using var malformed = await fixture.Client.GetAsync("/people?groupId=not-a-guid", Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var memberRemoved = await fixture.Client.DeleteAsync($"/person-groups/{group.Id}/members/{data.Person}", Cancellation);
            Assert.Equal(HttpStatusCode.NoContent, memberRemoved.StatusCode);
        }
        using var unset = await fixture.Client.PutAsJsonAsync($"/people/{data.Person}/favorite", new { IsFavorite = false }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, unset.StatusCode);
        Assert.False((await unset.Content.ReadFromJsonAsync<SocialTelemetry.Application.Features.People.SetFavorite.Result>(Cancellation))!.IsFavorite);
        using var removed = await fixture.Client.DeleteAsync($"/person-groups/{group.Id}", Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var again = await fixture.Client.DeleteAsync($"/person-groups/{group.Id}", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.NotNull(await fixture.FindPersonAsync(data.Person));
        await using var checking = fixture.CreateAsyncScope();
        var database = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await database.PersonGroupMemberships.AnyAsync(link => link.GroupId == second.Id && link.PersonId == data.Person, Cancellation));
        Assert.False(await database.PersonGroupMemberships.AnyAsync(link => link.GroupId == group.Id, Cancellation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Direct_name_validation_and_required_favorite_are_authoritative(string? name)
    {
        var data = await SeedAsync();
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider, data.Owner);
        await Assert.ThrowsAsync<FeatureValidationException>(() => scope.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync(name, Cancellation));
        await Assert.ThrowsAsync<FeatureValidationException>(() => scope.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync(new string('n', 201), Cancellation));
        await Assert.ThrowsAsync<FeatureValidationException>(() => scope.ServiceProvider.GetRequiredService<Favorite>().ExecuteAsync(data.Person, null, Cancellation));
        var group = await scope.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync(new string('n', 200), Cancellation);
        Assert.Equal(200, group.Name.Length);
        var renamed = await scope.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.PersonGroups.Rename.Scenario>()
            .ExecuteAsync(group.Id, "  " + group.Name + "  ", Cancellation);
        Assert.Equal(group.UpdatedAt, renamed.UpdatedAt);
    }

    [Fact]
    public async Task Direct_admission_cannot_be_forged_and_retained_scope_cannot_follow_switch()
    {
        var data = await SeedAsync();
        await using var forged = fixture.CreateAsyncScope();
        forged.ServiceProvider.GetRequiredService<LocalProfileOperation>().Bind(new(data.Owner, Guid.NewGuid()), Cancellation);
        await Assert.ThrowsAsync<LocalAccessException>(() => forged.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("Denied", Cancellation));
        await using var retained = fixture.CreateAsyncScope();
        await BindAsync(retained.ServiceProvider, data.Owner);
        var other = await fixture.CreateUserProfileAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retained.ServiceProvider.GetRequiredService<Favorite>().ExecuteAsync(data.Person, true, Cancellation));
        Assert.False((await fixture.FindPersonAsync(data.Person))!.IsFavorite);
        await using var current = fixture.CreateAsyncScope();
        await BindAsync(current.ServiceProvider, other);
        await Assert.ThrowsAsync<NotFoundException>(() => current.ServiceProvider.GetRequiredService<Favorite>().ExecuteAsync(data.Person, true, Cancellation));
    }

    [Fact]
    public async Task Foreign_group_person_and_filter_are_unavailable_even_for_noop_membership()
    {
        var first = await SeedAsync();
        Guid group;
        await using (var scope = fixture.CreateAsyncScope())
        {
            await BindAsync(scope.ServiceProvider, first.Owner);
            group = (await scope.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("Private group", Cancellation)).Id;
        }
        var second = await SeedAsync();
        foreach (var route in new[] { $"/person-groups/{group}", $"/person-groups/{group}/members", $"/people?groupId={group}" })
        {
            using var denied = await fixture.Client.GetAsync(route, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        await using var active = fixture.CreateAsyncScope();
        await BindAsync(active.ServiceProvider, second.Owner);
        var own = await active.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("Own", Cancellation);
        await Assert.ThrowsAsync<NotFoundException>(() => active.ServiceProvider.GetRequiredService<AddMember>().ExecuteAsync(own.Id, first.Person, Cancellation));
        await Assert.ThrowsAsync<NotFoundException>(() => active.ServiceProvider.GetRequiredService<RemoveMember>().ExecuteAsync(own.Id, first.Person, Cancellation));
        await Assert.ThrowsAsync<NotFoundException>(() => active.ServiceProvider.GetRequiredService<AddMember>().ExecuteAsync(group, second.Person, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_pending_work_is_refused_without_saving_or_discarding_it(bool favorite)
    {
        var data = await SeedAsync();
        await using var scope = fixture.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider, data.Owner);
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var person = await database.People.SingleAsync(row => row.Id == data.Person, Cancellation);
        person.Notes = "Caller pending work";
        if (favorite)
            await Assert.ThrowsAsync<ConflictException>(() => scope.ServiceProvider.GetRequiredService<Favorite>().ExecuteAsync(data.Person, true, Cancellation));
        else
            await Assert.ThrowsAsync<ConflictException>(() => scope.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("No save", Cancellation));
        Assert.Equal(EntityState.Modified, database.Entry(person).State);
        Assert.Equal("Caller pending work", person.Notes);
        Assert.Equal("Retained notes", (await fixture.FindPersonAsync(data.Person))!.Notes);
    }

    [Theory]
    [InlineData("favorite")]
    [InlineData("group")]
    [InlineData("membership")]
    public async Task Account_security_save_refuses_organizational_writes(string mutation)
    {
        var data = await SeedAsync();
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserProfileId = data.Owner, UserName = "synthetic" });
        if (mutation == "favorite") (await database.People.SingleAsync(person => person.Id == data.Person, Cancellation)).IsFavorite = true;
        else
        {
            var group = new PersonGroup { Id = Guid.NewGuid(), UserProfileId = data.Owner, Name = "Pending" };
            database.PersonGroups.Add(group);
            if (mutation == "membership") database.PersonGroupMemberships.Add(new() { GroupId = group.Id, PersonId = data.Person });
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync(Cancellation));
        Assert.True(database.ChangeTracker.HasChanges());
        await using var check = fixture.CreateAsyncScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().Users.AnyAsync(account => account.UserProfileId == data.Owner, Cancellation));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("person")]
    [InlineData("profile")]
    public async Task Legacy_foreign_membership_refuses_affected_cascade_and_preserves_both_sides(string target)
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        Guid groupId;
        await using (var seed = fixture.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var group = new PersonGroup { Id = Guid.NewGuid(), UserProfileId = second.Owner, Name = "Legacy" };
            database.PersonGroups.Add(group); await database.SaveChangesAsync(Cancellation); groupId = group.Id;
            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"PersonGroupMemberships\" (\"GroupId\", \"PersonId\") VALUES ({groupId}, {first.Person})", Cancellation);
        }
        await fixture.SelectProfileAsync(target == "group" ? second.Owner : first.Owner);
        await using var active = fixture.CreateAsyncScope();
        await BindAsync(active.ServiceProvider, target == "group" ? second.Owner : first.Owner);
        var stored = active.ServiceProvider.GetRequiredService<AppDbContext>();
        if (target == "group") await Assert.ThrowsAsync<ConflictException>(() => active.ServiceProvider.GetRequiredService<DeleteGroup>().ExecuteAsync(groupId, Cancellation));
        else if (target == "person")
            await Assert.ThrowsAsync<ConflictException>(() => active.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.People.Delete.Scenario>().ExecuteAsync(new() { Id = first.Person }, Cancellation));
        else
        {
            var selection = await active.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
            await Assert.ThrowsAsync<ConflictException>(() => active.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.LocalProfiles.Delete.Scenario>()
                .ExecuteAsync(first.Owner, selection.Version, true, null, Cancellation));
        }
        await using var checking = fixture.CreateAsyncScope();
        var databaseAfter = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await databaseAfter.PersonGroupMemberships.AnyAsync(link => link.GroupId == groupId && link.PersonId == first.Person, Cancellation));
        Assert.NotNull(await fixture.FindPersonAsync(first.Person));
        Assert.NotNull(await fixture.FindPersonAsync(second.Person));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_or_commit_failure_rolls_back_organization(bool commit)
    {
        var data = await SeedAsync();
        var fault = new MutationFault(commit);
        using var application = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(fault)));
        await using var scope = application.Services.CreateAsyncScope();
        await BindAsync(scope.ServiceProvider, data.Owner);
        fault.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("Rolled back", Cancellation));
        await using var check = fixture.CreateAsyncScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().PersonGroups.AnyAsync(group => group.UserProfileId == data.Owner, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_hosts_add_add_or_add_remove_are_serialized_without_changing_metadata(bool removeSecond)
    {
        var data = await SeedAsync();
        var commitGate = new CommitGate(false);
        var guardAttempt = new GuardAttempt();
        using var firstHost = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(commitGate)));
        using var secondHost = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(guardAttempt)));
        _ = firstHost.Services; _ = secondHost.Services;
        Guid group;
        DateTimeOffset timestamp;
        await using (var seed = fixture.CreateAsyncScope())
        {
            await BindAsync(seed.ServiceProvider, data.Owner);
            var created = await seed.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("Shared", Cancellation);
            group = created.Id; timestamp = created.UpdatedAt;
        }
        await using var first = firstHost.Services.CreateAsyncScope();
        await using var second = secondHost.Services.CreateAsyncScope();
        await BindAsync(first.ServiceProvider, data.Owner); await BindAsync(second.ServiceProvider, data.Owner);
        commitGate.Armed = true;
        guardAttempt.Target = second.ServiceProvider.GetRequiredService<AppDbContext>();
        var firstAdd = first.ServiceProvider.GetRequiredService<AddMember>().ExecuteAsync(group, data.Person, Cancellation);
        await commitGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        // Run the second BEGIN on a separate worker: SQLite's driver waits synchronously.
        var secondAdd = Task.Run(() => removeSecond
            ? second.ServiceProvider.GetRequiredService<RemoveMember>().ExecuteAsync(group, data.Person, Cancellation)
            : second.ServiceProvider.GetRequiredService<AddMember>().ExecuteAsync(group, data.Person, Cancellation), Cancellation);
        try { await guardAttempt.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation); }
        finally { commitGate.Release.TrySetResult(); }
        await Task.WhenAll(firstAdd, secondAdd);
        await using var check = fixture.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(removeSecond ? 0 : 1, await database.PersonGroupMemberships.CountAsync(link => link.GroupId == group, Cancellation));
        Assert.Equal(timestamp, (await database.PersonGroups.SingleAsync(row => row.Id == group, Cancellation)).UpdatedAt);
        await first.ServiceProvider.GetRequiredService<RemoveMember>().ExecuteAsync(group, data.Person, Cancellation);
        await second.ServiceProvider.GetRequiredService<RemoveMember>().ExecuteAsync(group, data.Person, Cancellation);
        await database.Entry(await database.PersonGroups.SingleAsync(row => row.Id == group, Cancellation)).ReloadAsync(Cancellation);
        Assert.False(await database.PersonGroupMemberships.AnyAsync(link => link.GroupId == group, Cancellation));
    }

    [Fact]
    public async Task Tracked_membership_with_different_owners_is_rejected_without_persistence()
    {
        var first = await SeedAsync(); var second = await SeedAsync();
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var group = new PersonGroup { Id = Guid.NewGuid(), UserProfileId = second.Owner, Name = "Foreign link" };
        database.PersonGroups.Add(group); database.PersonGroupMemberships.Add(new() { GroupId = group.Id, PersonId = first.Person });
        await Assert.ThrowsAsync<ConflictException>(() => database.SaveChangesAsync(Cancellation));
        await using var checking = fixture.CreateAsyncScope();
        Assert.False(await checking.ServiceProvider.GetRequiredService<AppDbContext>().PersonGroups.AnyAsync(row => row.Id == group.Id, Cancellation));
    }

    [Fact]
    public async Task Upgrade_from_published_b35_schema_preserves_ids_store_and_false_default()
    {
        var directory = Directory.CreateTempSubdirectory("SocialTelemetry.Groups.Upgrade.");
        var schema = "groups_" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<AppDbContext>();
        await using var fixtureScope = fixture.CreateAsyncScope();
        var configured = fixtureScope.ServiceProvider.GetRequiredService<AppDbContext>();
        NpgsqlConnection? admin = null;
        try
        {
            if (configured.Database.IsSqlite())
                options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.FullName, "upgrade.db"), Pooling = false }.ToString());
            else
            {
                admin = new NpgsqlConnection(fixture.DisposablePostgresConnection);
                await admin.OpenAsync(Cancellation);
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
                await create.ExecuteNonQueryAsync(Cancellation);
                options.UseNpgsql(new NpgsqlConnectionStringBuilder(fixture.DisposablePostgresConnection) { SearchPath = schema }.ToString());
            }
            var owner = Guid.NewGuid(); var person = Guid.NewGuid(); var store = Guid.NewGuid();
            await using (var previous = new AppDbContext(options.Options))
            {
                var baseline = previous.Database.GetMigrations().Single(id => id.EndsWith("_AddProfileDeletionCleanup", StringComparison.Ordinal));
                await previous.GetService<IMigrator>().MigrateAsync(baseline, Cancellation);
                previous.UserProfiles.Add(new() { Id = owner, DisplayName = "Before groups" });
                previous.StorageDatabaseIdentities.Add(new() { Id = 1, StoreId = store });

                await previous.SaveChangesAsync(Cancellation);
                object timestamp = previous.Database.IsSqlite() ? DateTimeOffset.UnixEpoch.UtcTicks : DateTimeOffset.UnixEpoch;
                await previous.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "People" ("Id", "UserProfileId", "DisplayName", "Notes", "RelationshipContext", "CreatedAt", "UpdatedAt")
                    VALUES ({person}, {owner}, {"Retained person"}, {"Retained"}, {"Unknown"}, {timestamp}, {timestamp})
                    """, Cancellation);
            }
            await using var upgraded = new AppDbContext(options.Options);
            await upgraded.Database.MigrateAsync(Cancellation);
            Assert.False(upgraded.Database.HasPendingModelChanges());
            Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync(Cancellation));
            var retained = await upgraded.People.SingleAsync(row => row.Id == person, Cancellation);
            Assert.False(retained.IsFavorite); Assert.Equal(owner, retained.UserProfileId); Assert.Equal("Retained", retained.Notes);
            Assert.Equal(store, (await upgraded.StorageDatabaseIdentities.SingleAsync(Cancellation)).StoreId);
            Assert.Empty(await upgraded.PersonGroups.ToArrayAsync(Cancellation));
            Assert.Empty(await upgraded.PersonGroupMemberships.ToArrayAsync(Cancellation));
        }
        finally
        {
            if (admin is not null)
            {
                await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
                await drop.ExecuteNonQueryAsync(CancellationToken.None); await admin.DisposeAsync();
            }
            directory.Delete(true); // The exact newly generated test directory only.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_group_response_survives_later_journal_deletion(bool otherHost)
    {
        var data = await SeedAsync();
        var gate = new CommitGate(true);
        using var responseHost = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(gate)));
        using var secondHost = fixture.WithServices(_ => { });
        using var client = responseHost.CreateClient(); fixture.ConfigureProfileClient(client);
        _ = secondHost.Services;
        gate.Armed = true;
        var responseTask = client.PostAsJsonAsync("/person-groups", new { Name = "Commit winner" }, Cancellation);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        try
        {
            await using var deletion = (otherHost ? secondHost.Services : responseHost.Services).CreateAsyncScope();
            await BindAsync(deletion.ServiceProvider, data.Owner);
            var selection = await deletion.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
            await deletion.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.LocalProfiles.Delete.Scenario>()
                .ExecuteAsync(data.Owner, selection.Version, true, null, Cancellation);
            if (otherHost)
            {
                await using var refresh = responseHost.Services.CreateAsyncScope();
                await refresh.ServiceProvider.GetRequiredService<LocalAccessManagement>().ExpireAsync(Cancellation);
            }
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var check = fixture.CreateAsyncScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().PersonGroups.AnyAsync(group => group.UserProfileId == data.Owner, Cancellation));
    }

    [Fact]
    public async Task Cooperating_host_switch_rejects_retained_admission_and_stale_HTTP_assertion()
    {
        var data = await SeedAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        using var secondHost = fixture.WithServices(_ => { });
        await using var retained = fixture.CreateAsyncScope();
        await BindAsync(retained.ServiceProvider, data.Owner);
        var original = retained.ServiceProvider.GetRequiredService<LocalProfileOperation>();
        await using (var selecting = secondHost.Services.CreateAsyncScope())
        {
            var selector = selecting.ServiceProvider.GetRequiredService<LocalProfileSelectionService>();
            var before = await selector.GetAsync(Cancellation);
            await selector.SelectAsync(other, before.Version, Cancellation);
        }
        Assert.False(original.Lifetime.IsCancellationRequested); // The callback host deliberately has not refreshed.
        var denied = await Assert.ThrowsAsync<LocalProfileAccessException>(() => retained.ServiceProvider.GetRequiredService<Favorite>().ExecuteAsync(data.Person, true, Cancellation));
        Assert.Equal(LocalProfileFailure.SelectionChanged, denied.Failure);
        using var stale = await fixture.Client.PostAsJsonAsync("/person-groups", new { Name = "Stale" }, Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.False((await fixture.FindPersonAsync(data.Person))!.IsFavorite);
    }

    [Fact]
    public async Task Owned_profile_cascade_removes_groups_and_memberships_without_touching_other_journal()
    {
        var data = await SeedAsync();
        var other = await fixture.CreateUserProfileAsync(select: false);
        await using (var active = fixture.CreateAsyncScope())
        {
            await BindAsync(active.ServiceProvider, data.Owner);
            var group = await active.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("Delete with journal", Cancellation);
            await active.ServiceProvider.GetRequiredService<AddMember>().ExecuteAsync(group.Id, data.Person, Cancellation);
            var selection = await active.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
            await active.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.LocalProfiles.Delete.Scenario>()
                .ExecuteAsync(data.Owner, selection.Version, true, null, Cancellation);
        }
        await using var check = fixture.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.PersonGroups.AnyAsync(group => group.UserProfileId == data.Owner, Cancellation));
        Assert.False(await database.PersonGroupMemberships.AnyAsync(link => link.PersonId == data.Person, Cancellation));
        Assert.True(await database.UserProfiles.AnyAsync(profile => profile.Id == other, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_then_waiting_group_or_person_delete_preserves_atomic_graph(bool deletePerson)
    {
        var data = await SeedAsync();
        Guid groupId;
        await using (var setup = fixture.CreateAsyncScope())
        {
            await BindAsync(setup.ServiceProvider, data.Owner);
            var group = await setup.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("Before", Cancellation);
            groupId = group.Id;
            await setup.ServiceProvider.GetRequiredService<AddMember>().ExecuteAsync(groupId, data.Person, Cancellation);
        }
        var commit = new CommitGate(false); var attempted = new GuardAttempt();
        using var renameHost = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(commit)));
        using var deleteHost = fixture.WithServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(attempted)));
        await using var renaming = renameHost.Services.CreateAsyncScope();
        await using var deleting = deleteHost.Services.CreateAsyncScope();
        await BindAsync(renaming.ServiceProvider, data.Owner); await BindAsync(deleting.ServiceProvider, data.Owner);
        commit.Armed = true; attempted.Target = deleting.ServiceProvider.GetRequiredService<AppDbContext>();
        var rename = renaming.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.PersonGroups.Rename.Scenario>()
            .ExecuteAsync(groupId, "Commit winner", Cancellation);
        await commit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
        var delete = Task.Run(async () =>
        {
            if (deletePerson)
                await deleting.ServiceProvider.GetRequiredService<SocialTelemetry.Application.Features.People.Delete.Scenario>().ExecuteAsync(new() { Id = data.Person }, Cancellation);
            else await deleting.ServiceProvider.GetRequiredService<DeleteGroup>().ExecuteAsync(groupId, Cancellation);
        }, Cancellation);
        try { await attempted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Cancellation); }
        finally { commit.Release.TrySetResult(); }
        await rename; await delete;
        await using var checking = fixture.CreateAsyncScope();
        var database = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.PersonGroupMemberships.AnyAsync(link => link.GroupId == groupId, Cancellation));
        Assert.Equal(deletePerson, await database.PersonGroups.AnyAsync(group => group.Id == groupId, Cancellation));
        Assert.Equal(!deletePerson, await database.People.AnyAsync(person => person.Id == data.Person, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detached_id_only_parent_delete_uses_durable_owner_and_removes_only_owned_membership(bool deleteGroup)
    {
        var data = await SeedAsync(); Guid groupId;
        await using (var setup = fixture.CreateAsyncScope())
        {
            await BindAsync(setup.ServiceProvider, data.Owner);
            groupId = (await setup.ServiceProvider.GetRequiredService<GroupCreate>().ExecuteAsync("Retained group", Cancellation)).Id;
            await setup.ServiceProvider.GetRequiredService<AddMember>().ExecuteAsync(groupId, data.Person, Cancellation);
        }
        await using (var deletion = fixture.CreateAsyncScope())
        {
            await BindAsync(deletion.ServiceProvider, data.Owner);
            var database = deletion.ServiceProvider.GetRequiredService<AppDbContext>();
            if (deleteGroup) database.PersonGroups.Remove(new PersonGroup { Id = groupId });
            else database.People.Remove(new Person { Id = data.Person });
            await database.SaveChangesAsync(Cancellation);
        }
        await using var checking = fixture.CreateAsyncScope();
        var after = checking.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await after.PersonGroupMemberships.AnyAsync(link => link.GroupId == groupId, Cancellation));
        Assert.Equal(!deleteGroup, await after.PersonGroups.AnyAsync(group => group.Id == groupId, Cancellation));
        Assert.Equal(deleteGroup, await after.People.AnyAsync(person => person.Id == data.Person, Cancellation));
    }

    private sealed class GuardAttempt : DbTransactionInterceptor
    {
        public AppDbContext? Target { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            // SQLite waits for the writer at BEGIN itself, before the selector UPDATE.
            if (eventData.Context == Target) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommitGate(bool afterCommit) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task PauseAsync(TransactionEventData eventData)
        {
            if (!Armed || !eventData.Context!.ChangeTracker.Entries().Any(entry =>
                    entry.Entity is PersonGroupMembership || entry.Entity is PersonGroup { Name: "Commit winner" })) return;
            Armed = false; Entered.TrySetResult();
            // A fixture-only post-commit observer deliberately ignores owner cancellation.
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit) await PauseAsync(eventData);
            return result;
        }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (afterCommit) await PauseAsync(eventData);
        }
    }

    private sealed class MutationFault(bool commit) : DbTransactionInterceptor, ISaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && commit) { Armed = false; throw new InvalidOperationException("Synthetic organization commit failure"); }
            return ValueTask.FromResult(result);
        }
        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !commit && eventData.Context!.ChangeTracker.Entries<PersonGroup>().Any())
            { Armed = false; throw new InvalidOperationException("Synthetic organization save failure"); }
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class FavoritesGroupsTests(PeopleApiFixture fixture) : FavoritesGroupsTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteFavoritesGroupsTests(SqliteApiFixture fixture) : FavoritesGroupsTestsContract<SqliteApiFixture>(fixture);
