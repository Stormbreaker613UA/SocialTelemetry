using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.Analysis;
using System.Data.Common;
using System.Data;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.Users;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Infrastructure.Identity;

public abstract class IdentityFoundationTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Identity_and_Core_stores_share_the_scoped_context_and_rollback_together()
    {
        var profile = await fixture.CreateUserProfileAsync();
        var account = Account(profile);
        var clientId = "rollback-" + Guid.NewGuid().ToString("N");
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Same(database, scope.ServiceProvider.GetRequiredService<AppDbContext>());
            var store = scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
            var identityStore = Assert.IsAssignableFrom<Microsoft.AspNetCore.Identity.EntityFrameworkCore.UserOnlyStore<
                ApplicationUser, AppDbContext, Guid, IdentityUserClaim<Guid>, IdentityUserLogin<Guid>, IdentityUserToken<Guid>>>(store);
            Assert.Same(database, identityStore.Context);
            await using var transaction = await database.Database.BeginTransactionAsync(Cancellation);
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(account)).Succeeded);
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await applications.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId, ClientType = OpenIddictConstants.ClientTypes.Public
            }, Cancellation);
            Assert.True(await database.Users.AnyAsync(user => user.Id == account.Id, Cancellation));
            Assert.True(await database.Set<OpenIddictEntityFrameworkCoreApplication<Guid>>().AnyAsync(entry => entry.ClientId == clientId, Cancellation));
            await transaction.RollbackAsync(Cancellation);
        }
        await using var check = fixture.CreateAsyncScope();
        var reloaded = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await reloaded.Users.AnyAsync(user => user.Id == account.Id, Cancellation));
        Assert.False(await reloaded.Set<OpenIddictEntityFrameworkCoreApplication<Guid>>().AnyAsync(entry => entry.ClientId == clientId, Cancellation));
        Assert.True(await reloaded.UserProfiles.AnyAsync(user => user.Id == profile, Cancellation));
    }

    [Fact]
    public async Task Real_user_store_keeps_distinct_optional_profile_links_without_email_or_password()
    {
        var firstProfile = await fixture.CreateUserProfileAsync();
        var secondProfile = await fixture.CreateUserProfileAsync();
        var unprotectedProfile = await fixture.CreateUserProfileAsync();
        Guid firstAccountId;
        Guid secondAccountId;
        var lockoutEnd = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).AddMinutes(3);
        await using (var scope = fixture.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var first = Account(firstProfile);
            var second = Account(secondProfile);
            Assert.True((await manager.CreateAsync(first, "Synthetic-Foundation9!")).Succeeded);
            Assert.True((await manager.CreateAsync(second)).Succeeded);
            Assert.True((await manager.SetLockoutEndDateAsync(second, lockoutEnd)).Succeeded);
            Assert.True((await manager.AddClaimAsync(first, new Claim("synthetic", "fixture"))).Succeeded);
            Assert.True((await manager.AddLoginAsync(first, new UserLoginInfo("fixture", first.Id.ToString(), "Fixture"))).Succeeded);
            Assert.True((await manager.SetAuthenticationTokenAsync(first, "fixture", "fixture", "synthetic-token")).Succeeded);
            firstAccountId = first.Id;
            secondAccountId = second.Id;
        }
        await using var check = fixture.CreateAsyncScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var firstStored = Assert.IsType<ApplicationUser>(await users.FindByIdAsync(firstAccountId.ToString()));
        var secondStored = Assert.IsType<ApplicationUser>(await users.FindByIdAsync(secondAccountId.ToString()));
        Assert.NotEqual(Guid.Empty, firstStored.Id);
        Assert.NotEqual(firstStored.Id, secondStored.Id);
        Assert.NotEqual(firstStored.Id, firstProfile);
        Assert.Equal(firstProfile, firstStored.UserProfileId);
        Assert.Equal(secondProfile, secondStored.UserProfileId);
        Assert.Null(firstStored.Email);
        Assert.Null(secondStored.Email);
        Assert.True(await users.CheckPasswordAsync(firstStored, "Synthetic-Foundation9!"));
        Assert.False(await users.CheckPasswordAsync(firstStored, "Wrong-Foundation9!"));
        Assert.NotEqual("Synthetic-Foundation9!", firstStored.PasswordHash);
        Assert.False(await users.HasPasswordAsync(secondStored));
        Assert.Equal(lockoutEnd, secondStored.LockoutEnd);
        Assert.Single(await users.GetClaimsAsync(firstStored));
        Assert.Single(await users.GetLoginsAsync(firstStored));
        Assert.Equal("synthetic-token", await users.GetAuthenticationTokenAsync(firstStored, "fixture", "fixture"));
        Assert.False(await users.Users.AnyAsync(account => account.UserProfileId == unprotectedProfile, Cancellation));
    }

    [Fact]
    public async Task Database_rejects_duplicate_and_dangling_account_links()
    {
        var profile = await fixture.CreateUserProfileAsync();
        await using (var first = fixture.CreateAsyncScope())
        {
            var database = first.ServiceProvider.GetRequiredService<AppDbContext>();
            database.Users.Add(Account(profile));
            await database.SaveChangesAsync(Cancellation);
        }
        foreach (var owner in new[] { profile, Guid.NewGuid() })
        {
            await using var scope = fixture.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.Users.Add(Account(owner));
            await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));
        }
    }

    [Fact]
    public async Task Concurrent_claims_of_one_profile_have_exactly_one_winner()
    {
        var profile = await fixture.CreateUserProfileAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ClaimAsync();
        var second = ClaimAsync();
        start.SetResult();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Equal(1, outcomes.Count(success => success));
        await using var check = fixture.CreateAsyncScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .CountAsync(account => account.UserProfileId == profile, Cancellation));

        async Task<bool> ClaimAsync()
        {
            await using var scope = fixture.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.Users.Add(Account(profile));
            await start.Task.WaitAsync(Cancellation);
            try { await database.SaveChangesAsync(Cancellation); return true; }
            catch (DbUpdateException exception)
            {
                Assert.True(exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                    or SqliteException { SqliteExtendedErrorCode: 2067 });
                return false;
            }
        }
    }

    [Fact]
    public async Task Account_deletion_preserves_journal_and_linked_profile_deletion_is_restricted()
    {
        var profile = await fixture.CreateUserProfileAsync();
        var person = await fixture.CreatePersonAsync(new() { UserProfileId = profile, DisplayName = "Synthetic person" });
        Guid accountId;
        Guid interactionId;
        Guid attachmentId;
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var account = Account(profile);
            database.Users.Add(account);
            var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = profile, Title = "Synthetic journal" };
            var attachment = new InteractionAttachment
            {
                Id = Guid.NewGuid(), InteractionId = interaction.Id, Type = AttachmentType.Text, TextContent = "Synthetic evidence"
            };
            database.Interactions.Add(interaction);
            database.InteractionParticipants.Add(new() { InteractionId = interaction.Id, PersonId = person });
            database.InteractionAttachments.Add(attachment);
            database.InteractionAnalyses.Add(new() { Id = Guid.NewGuid(), InteractionId = interaction.Id, Summary = "Historical analysis" });
            await database.SaveChangesAsync(Cancellation);
            accountId = account.Id;
            interactionId = interaction.Id;
            attachmentId = attachment.Id;
        }
        await using (var blocked = fixture.CreateAsyncScope())
        {
            var database = blocked.ServiceProvider.GetRequiredService<AppDbContext>();
            database.UserProfiles.Remove(await database.UserProfiles.SingleAsync(stored => stored.Id == profile, Cancellation));
            await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Cancellation));
        }
        await using (var remove = fixture.CreateAsyncScope())
        {
            var manager = remove.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = Assert.IsType<ApplicationUser>(await manager.FindByIdAsync(accountId.ToString()));
            Assert.True((await manager.DeleteAsync(account)).Succeeded);
        }
        await using var check = fixture.CreateAsyncScope();
        var remaining = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await remaining.Users.AnyAsync(account => account.Id == accountId, Cancellation));
        Assert.True(await remaining.UserProfiles.AnyAsync(stored => stored.Id == profile, Cancellation));
        Assert.True(await remaining.People.AnyAsync(stored => stored.Id == person, Cancellation));
        Assert.True(await remaining.Interactions.AnyAsync(stored => stored.Id == interactionId, Cancellation));
        Assert.True(await remaining.InteractionParticipants.AnyAsync(stored => stored.InteractionId == interactionId && stored.PersonId == person, Cancellation));
        Assert.True(await remaining.InteractionAttachments.AnyAsync(stored => stored.Id == attachmentId, Cancellation));
        Assert.True(await remaining.InteractionAnalyses.AnyAsync(stored => stored.InteractionId == interactionId, Cancellation));
    }

    [Fact]
    public async Task User_store_concurrency_rejects_a_stale_account_update()
    {
        var profile = await fixture.CreateUserProfileAsync();
        var account = Account(profile);
        await using var firstScope = fixture.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await first.CreateAsync(account)).Succeeded);
        await using var secondScope = fixture.CreateAsyncScope();
        var second = secondScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var stale = Assert.IsType<ApplicationUser>(await second.FindByIdAsync(account.Id.ToString()));
        account.PhoneNumber = "synthetic-first";
        Assert.True((await first.UpdateAsync(account)).Succeeded);
        stale.PhoneNumber = "synthetic-stale";
        var conflict = await second.UpdateAsync(stale);
        Assert.False(conflict.Succeeded);
        Assert.Contains(conflict.Errors, error => error.Code == "ConcurrencyFailure");
    }

    [Fact]
    public async Task All_four_core_managers_persist_guid_entities_without_protocol_activation()
    {
        string applicationId;
        string authorizationId;
        string tokenId;
        string scopeId;
        var clientId = "fixture-" + Guid.NewGuid().ToString("N");
        await using (var scope = fixture.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await applications.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId, ClientType = OpenIddictConstants.ClientTypes.Public
            }, Cancellation);
            applicationId = Assert.IsType<string>(await applications.GetIdAsync(application, Cancellation));
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
            {
                ApplicationId = applicationId, Subject = "synthetic-subject",
                Status = OpenIddictConstants.Statuses.Valid, Type = OpenIddictConstants.AuthorizationTypes.Permanent
            }, Cancellation);
            authorizationId = Assert.IsType<string>(await authorizations.GetIdAsync(authorization, Cancellation));
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
            {
                ApplicationId = applicationId, AuthorizationId = authorizationId, Subject = "synthetic-subject",
                Status = OpenIddictConstants.Statuses.Valid, Type = OpenIddictConstants.TokenTypeHints.RefreshToken
            }, Cancellation);
            tokenId = Assert.IsType<string>(await tokens.GetIdAsync(token, Cancellation));
            var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
            var entry = await scopes.CreateAsync(new OpenIddictScopeDescriptor { Name = clientId }, Cancellation);
            scopeId = Assert.IsType<string>(await scopes.GetIdAsync(entry, Cancellation));
        }
        await using var check = fixture.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await database.Set<OpenIddictEntityFrameworkCoreApplication<Guid>>().AnyAsync(entry => entry.Id == Guid.Parse(applicationId), Cancellation));
        Assert.True(await database.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>().AnyAsync(entry => entry.Id == Guid.Parse(authorizationId), Cancellation));
        Assert.True(await database.Set<OpenIddictEntityFrameworkCoreToken<Guid>>().AnyAsync(entry => entry.Id == Guid.Parse(tokenId), Cancellation));
        Assert.True(await database.Set<OpenIddictEntityFrameworkCoreScope<Guid>>().AnyAsync(entry => entry.Id == Guid.Parse(scopeId), Cancellation));
    }

    [Fact]
    public async Task Account_changes_do_not_enter_context_or_acquire_social_guards()
    {
        var profile = await fixture.CreateUserProfileAsync();
        var person = await fixture.CreatePersonAsync(new() { UserProfileId = profile, DisplayName = "Synthetic participant" });
        Guid interactionId;
        await using (var seed = fixture.CreateAsyncScope())
        {
            var database = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var interaction = new Interaction { Id = Guid.NewGuid(), UserProfileId = profile, Title = "Synthetic context" };
            database.Interactions.Add(interaction);
            database.InteractionParticipants.Add(new() { InteractionId = interaction.Id, PersonId = person });
            await database.SaveChangesAsync(Cancellation);
            interactionId = interaction.Id;
        }
        await using var scope = fixture.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<LocalProfileOperation>().Bind(new(profile, Guid.NewGuid()), CancellationToken.None);
        var builder = scope.ServiceProvider.GetRequiredService<AiContextBuilder>();
        var original = await builder.BuildAsync(interactionId, null, [], Cancellation);
        var observer = new GuardCommandObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>())
            .AddInterceptors(observer).Options;
        await using (var database = new AppDbContext(options))
        {
            var account = Account(profile);
            database.Users.Add(account);
            await database.SaveChangesAsync(Cancellation);
            account.SecurityStamp = Guid.NewGuid().ToString();
            await database.SaveChangesAsync(Cancellation);
        }
        Assert.Equal(0, observer.GuardCommands);
        var current = await builder.BuildAsync(interactionId, null, [], Cancellation);
        Assert.Equal(original.Fingerprint, current.Fingerprint);
        Assert.Equal(original.InputJson, current.InputJson);
    }

    [Fact]
    public async Task Prefoundation_upgrade_preserves_ids_store_identity_and_reviewed_audio_history()
    {
        await using var fixtureScope = fixture.CreateAsyncScope();
        var configured = fixtureScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DirectoryInfo? temporary = null;
        NpgsqlConnection? schemaOwner = null;
        var schemaCreated = false;
        var schema = "foundation_" + Guid.NewGuid().ToString("N");
        try
        {
            if (configured.Database.IsSqlite())
            {
                temporary = Directory.CreateTempSubdirectory("SocialTelemetry.Foundation.");
                options.UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(temporary.FullName, "upgrade.db"), ForeignKeys = true, Pooling = false
                }.ToString());
            }
            else
            {
                var connectionString = configured.GetService<IDbContextOptions>().Extensions
                    .OfType<RelationalOptionsExtension>().Single().ConnectionString;
                schemaOwner = new NpgsqlConnection(connectionString);
                await schemaOwner.OpenAsync(Cancellation);
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", schemaOwner);
                await create.ExecuteNonQueryAsync(Cancellation);
                schemaCreated = true;
                options.UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ToString());
            }
            var profileId = Guid.NewGuid();
            var interactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var analysisId = Guid.NewGuid();
            var transcriptVersion = Guid.NewGuid();
            var storeId = Guid.NewGuid();
            await using (var legacy = new AppDbContext(options.Options))
            {
                var previous = legacy.Database.GetMigrations().Single(migration => migration.EndsWith("AddAttachmentTranscripts", StringComparison.Ordinal));
                await legacy.GetService<IMigrator>().MigrateAsync(previous, Cancellation);
                legacy.UserProfiles.Add(new UserProfile { Id = profileId, DisplayName = "Legacy synthetic owner" });
                legacy.StorageDatabaseIdentities.Add(new() { Id = 1, StoreId = storeId });
                legacy.Interactions.Add(new() { Id = interactionId, UserProfileId = profileId, Title = "Legacy journal" });
                legacy.InteractionAttachments.Add(new()
                {
                    Id = attachmentId, InteractionId = interactionId, Type = AttachmentType.Audio,
                    StorageKey = new string('a', 32), MimeType = "audio/wav"
                });
                legacy.AttachmentTranscripts.Add(new()
                {
                    AttachmentId = attachmentId, GeneratedText = "Synthetic original", CorrectedText = "Synthetic correction",
                    ReviewStatus = TranscriptReviewStatus.Reviewed, ReviewedAt = DateTimeOffset.UtcNow,
                    SourceStorageKey = new string('a', 32), SourceSha256 = new string('B', 64), Version = transcriptVersion
                });
                legacy.InteractionAnalyses.Add(new() { Id = analysisId, InteractionId = interactionId, Summary = "Legacy analysis" });
                await legacy.SaveChangesAsync(Cancellation);
            }
            await using (var upgrade = new AppDbContext(options.Options))
            {
                await upgrade.Database.MigrateAsync(Cancellation);
                Assert.False(upgrade.Database.HasPendingModelChanges());
                Assert.Empty(await upgrade.Users.ToListAsync(Cancellation));
                upgrade.Users.Add(Account(profileId));
                await upgrade.SaveChangesAsync(Cancellation);
            }
            await using var reopened = new AppDbContext(options.Options);
            Assert.Equal(profileId, (await reopened.UserProfiles.SingleAsync(Cancellation)).Id);
            Assert.Equal(storeId, (await reopened.StorageDatabaseIdentities.SingleAsync(Cancellation)).StoreId);
            Assert.Equal(interactionId, (await reopened.Interactions.SingleAsync(Cancellation)).Id);
            Assert.Equal(attachmentId, (await reopened.InteractionAttachments.SingleAsync(Cancellation)).Id);
            Assert.Equal(analysisId, (await reopened.InteractionAnalyses.SingleAsync(Cancellation)).Id);
            var transcript = await reopened.AttachmentTranscripts.SingleAsync(Cancellation);
            Assert.Equal(transcriptVersion, transcript.Version);
            Assert.Equal(TranscriptReviewStatus.Reviewed, transcript.ReviewStatus);
            Assert.Equal("Synthetic correction", transcript.CorrectedText);
            Assert.Equal(profileId, (await reopened.Users.SingleAsync(Cancellation)).UserProfileId);
        }
        finally
        {
            if (schemaOwner is not null)
            {
                if (schemaCreated && schemaOwner.State == ConnectionState.Open)
                {
                    await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", schemaOwner);
                    await drop.ExecuteNonQueryAsync(CancellationToken.None);
                }
                await schemaOwner.DisposeAsync();
            }
            temporary?.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Model_is_roleless_matches_snapshot_and_adds_no_application_auth_surface()
    {
        await using var scope = fixture.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(database.Database.HasPendingModelChanges());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(Cancellation));
        var tables = database.Model.GetEntityTypes().Select(entity => entity.GetTableName()).ToHashSet();
        Assert.Equal(4, tables.Count(table => table?.StartsWith("AspNet", StringComparison.Ordinal) == true));
        Assert.Equal(4, tables.Count(table => table?.StartsWith("OpenIddict", StringComparison.Ordinal) == true));
        Assert.DoesNotContain("AspNetRoles", tables);
        Assert.DoesNotContain("AspNetUserPasskeys", tables);
        Assert.Null(scope.ServiceProvider.GetService<SignInManager<ApplicationUser>>());
        var schemes = scope.ServiceProvider.GetService<IAuthenticationSchemeProvider>();
        if (schemes is not null) Assert.Empty(await schemes.GetAllSchemesAsync());
        var endpoints = scope.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText is "/login" or "/register" or "/connect/token" or "/connect/authorize" or "/unlock");
        using var response = await fixture.Client.PostAsync("/connect/token", null, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static ApplicationUser Account(Guid profile) => new()
    {
        UserProfileId = profile, UserName = "fixture-" + Guid.NewGuid().ToString("N")
    };

    private sealed class GuardCommandObserver : DbCommandInterceptor
    {
        public int GuardCommands { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("AnalysisContextGuards", StringComparison.Ordinal)) GuardCommands++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("AnalysisContextGuards", StringComparison.Ordinal)) GuardCommands++;
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class IdentityFoundationTests(PeopleApiFixture fixture) : IdentityFoundationTestsContract<PeopleApiFixture>(fixture);

[Collection("SQLite")]
public sealed class SqliteIdentityFoundationTests(SqliteApiFixture fixture) : IdentityFoundationTestsContract<SqliteApiFixture>(fixture);
