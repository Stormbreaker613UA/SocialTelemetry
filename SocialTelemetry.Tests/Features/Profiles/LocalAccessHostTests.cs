using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class LocalAccessHostTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture> where TFixture : PeopleApiFixture
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_HTTPS_process_restart_preserves_only_opted_in_access_and_explicit_lock_survives_restart(bool remember)
    {
        string? connection = null;
        string? administrative = null;
        var schema = "local_access_" + Guid.NewGuid().ToString("N");
        await using (var scope = fixture.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (database.Database.IsNpgsql())
            {
                // EF's already-open connection intentionally redacts its password. Read only
                // this disposable fixture's original configuration for the owned child host.
                administrative = fixture.DisposablePostgresConnection!;
                await using var admin = new NpgsqlConnection(administrative);
                await admin.OpenAsync(Cancellation);
                await using var command = new NpgsqlCommand("CREATE SCHEMA \"" + schema + "\"", admin);
                await command.ExecuteNonQueryAsync(Cancellation);
                connection = new NpgsqlConnectionStringBuilder(administrative) { SearchPath = schema, Pooling = false }.ToString();
                await using var isolated = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
                await isolated.Database.MigrateAsync(Cancellation);
            }
        }
        try
        {
            await using var host = new LocalAccessProcessHost(connection);
            await host.StartAsync(Cancellation);
            using var client = host.CreateClient();
            using var initial = await GetAsync(client, "/local-profiles");
            var version = initial.RootElement.GetProperty("version").GetGuid();
            using var created = await SendAsync(client, HttpMethod.Post, "/local-profiles", new { DisplayName = "Synthetic restart profile", ExpectedVersion = version });
            var owner = created.RootElement.GetProperty("id").GetGuid();
            using var selected = await SendAsync(client, HttpMethod.Put, "/local-profiles/current", new { UserProfileId = owner, ExpectedVersion = version });
            Bind(client, owner, selected.RootElement.GetProperty("version").GetGuid());
            await CsrfAsync(client);
            var login = "restart-" + Guid.NewGuid().ToString("N");
            const string password = "Synthetic-Restart9!";
            using var enabled = await SendAsync(client, HttpMethod.Post, "/local-access/protection", new { Login = login, Password = password });
            using var signed = await SendAsync(client, HttpMethod.Post, "/local-access/sign-in", new
            {
                Login = login, Password = password, UserProfileId = owner,
                ExpectedVersion = enabled.RootElement.GetProperty("version").GetGuid(), Remember = remember
            });
            Bind(client, owner, signed.RootElement.GetProperty("version").GetGuid());
            using (var read = await client.GetAsync("/user-profile", Cancellation)) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            await host.StopAbruptlyAsync();
            await host.StartAsync(Cancellation);
            using (var read = await client.GetAsync("/user-profile", Cancellation))
                Assert.Equal(remember ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, read.StatusCode);
            // The device proof survives independently of the non-remembered account admission.
            await CsrfAsync(client);
            using var locked = await SendAsync(client, HttpMethod.Post, "/local-access/lock", new { UserProfileId = owner });
            Bind(client, owner, locked.RootElement.GetProperty("version").GetGuid());
            await host.StopAbruptlyAsync();
            await host.StartAsync(Cancellation);
            using var replay = await client.GetAsync("/user-profile", Cancellation);
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        }
        finally
        {
            if (administrative is not null)
            {
                await using var admin = new NpgsqlConnection(administrative);
                await admin.OpenAsync(CancellationToken.None);
                // Only the exact generated schema above, in this disposable fixture database.
                await using var command = new NpgsqlCommand("DROP SCHEMA \"" + schema + "\" CASCADE", admin);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route, Cancellation);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
    }
    private static async Task<JsonDocument> SendAsync(HttpClient client, HttpMethod method, string route, object body)
    {
        using var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Cancellation);
        Assert.True(response.IsSuccessStatusCode, "Synthetic local access request failed: " + route + " HTTP " + (int)response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
    }
    private static async Task CsrfAsync(HttpClient client)
    {
        using var token = await GetAsync(client, "/local-access/antiforgery");
        client.DefaultRequestHeaders.Remove("X-SocialTelemetry-CSRF");
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-CSRF", token.RootElement.GetProperty("requestToken").GetString());
    }
    private static void Bind(HttpClient client, Guid owner, Guid version)
    {
        client.DefaultRequestHeaders.Remove("X-SocialTelemetry-ProfileId");
        client.DefaultRequestHeaders.Remove("X-SocialTelemetry-ProfileVersion");
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-ProfileId", owner.ToString());
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-ProfileVersion", version.ToString());
    }
}

public sealed class LocalAccessHostTests(PeopleApiFixture fixture) : LocalAccessHostTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteLocalAccessHostTests(SqliteApiFixture fixture) : LocalAccessHostTestsContract<SqliteApiFixture>(fixture);
