using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Tests.Features.People;

namespace SocialTelemetry.Tests.Features.Profiles;

public abstract class LocalAccessHttpTestsContract<TFixture>(TFixture fixture) : IClassFixture<TFixture> where TFixture : PeopleApiFixture
{
    private const string Password = "Synthetic-HttpPassword9!";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Protected_HTTP_requires_HTTPS_cookie_and_CSRF_and_never_treats_owner_headers_as_authentication()
    {
        var owner = await fixture.CreateUserProfileAsync();
        using var application = fixture.WithServices(services => services.AddSingleton<IStartupFilter>(new LoopbackFilter()));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1");
        await SetSelectionHeadersAsync(application.Services, client, owner);
        await SetCsrfAsync(client);
        var login = "http-" + Guid.NewGuid().ToString("N");
        using (var enabled = await client.PostAsJsonAsync("/local-access/protection", new { Login = login, Password }, Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.Equal("no-store", enabled.Headers.CacheControl?.ToString());
        }
        await SetSelectionHeadersAsync(application.Services, client, owner);
        using (var denied = await client.GetAsync("/user-profile", Cancellation))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            var error = await denied.Content.ReadAsStringAsync(Cancellation);
            Assert.DoesNotContain(Password, error);
            Assert.Contains("Required", error);
        }
        var selection = await SelectionAsync(application.Services);
        using (var signed = await client.PostAsJsonAsync("/local-access/sign-in", new
            { Login = login, Password, UserProfileId = owner, ExpectedVersion = selection.Version, Remember = true }, Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
            var cookie = Assert.Single(signed.Headers.GetValues("Set-Cookie"), value => value.StartsWith("SocialTelemetry.LocalDevice=", StringComparison.Ordinal));
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
            var body = await signed.Content.ReadAsStringAsync(Cancellation);
            Assert.DoesNotContain("deviceToken", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Password, body);
        }
        await SetSelectionHeadersAsync(application.Services, client, owner);
        await SetCsrfAsync(client); // The authenticated device has a new antiforgery identity.
        using (var read = await client.GetAsync("/user-profile", Cancellation)) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        client.DefaultRequestHeaders.Remove("X-SocialTelemetry-CSRF");
        using (var rejected = await client.PutAsJsonAsync("/local-profiles/current", new { UserProfileId = owner, ExpectedVersion = (await SelectionAsync(application.Services)).Version }, Cancellation))
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using (var rejected = await client.PutAsJsonAsync("/user-profile", new { DisplayName = "Forbidden CSRF change" }, Cancellation))
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        await SetCsrfAsync(client);
        client.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        using (var rejected = await client.GetAsync("/user-profile", Cancellation)) Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        using (var locked = await client.PostAsJsonAsync("/local-access/lock", new { UserProfileId = owner }, Cancellation)) Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        await SetSelectionHeadersAsync(application.Services, client, owner);
        using (var denied = await client.GetAsync("/user-profile", Cancellation)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var insecure = application.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        insecure.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1");
        using var http = await insecure.PostAsJsonAsync("/local-access/sign-in", new { Login = login, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, http.StatusCode);
        Assert.Null(http.Headers.Location);
    }

    [Fact]
    public async Task Picker_source_and_workspace_mode_are_safe_independent_metadata()
    {
        await fixture.CreateUserProfileAsync();
        using var application = fixture.WithServices(services => services.AddSingleton<IStartupFilter>(new LoopbackFilter()));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-Local", "1");
        using var response = await client.GetAsync("/local-profiles", Cancellation);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal("Local", json.RootElement.GetProperty("mode").GetString());
        foreach (var profile in json.RootElement.GetProperty("profiles").EnumerateArray())
        {
            Assert.Equal("Local", profile.GetProperty("source").GetString());
            Assert.True(profile.TryGetProperty("protection", out _));
            Assert.False(profile.TryGetProperty("passwordHash", out _));
            Assert.False(profile.TryGetProperty("aboutMe", out _));
            Assert.False(profile.TryGetProperty("recoveryCode", out _));
        }
    }

    private static async Task SetCsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/local-access/antiforgery", Cancellation);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        client.DefaultRequestHeaders.Remove("X-SocialTelemetry-CSRF");
        client.DefaultRequestHeaders.Add("X-SocialTelemetry-CSRF", body.GetProperty("requestToken").GetString());
    }

    private static async Task<LocalProfileSelection> SelectionAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LocalProfileSelectionService>().GetAsync(Cancellation);
    }

    private static async Task SetSelectionHeadersAsync(IServiceProvider services, HttpClient client, Guid owner)
    {
        var selected = await SelectionAsync(services);
        client.DefaultRequestHeaders.Remove(LocalProfileMiddleware.OwnerHeader);
        client.DefaultRequestHeaders.Remove(LocalProfileMiddleware.VersionHeader);
        client.DefaultRequestHeaders.Add(LocalProfileMiddleware.OwnerHeader, owner.ToString());
        client.DefaultRequestHeaders.Add(LocalProfileMiddleware.VersionHeader, selected.Version.ToString());
    }

    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                await nextMiddleware();
            });
            next(application);
        };
    }
}

public sealed class LocalAccessHttpTests(PeopleApiFixture fixture) : LocalAccessHttpTestsContract<PeopleApiFixture>(fixture);
public sealed class SqliteLocalAccessHttpTests(SqliteApiFixture fixture) : LocalAccessHttpTestsContract<SqliteApiFixture>(fixture);
