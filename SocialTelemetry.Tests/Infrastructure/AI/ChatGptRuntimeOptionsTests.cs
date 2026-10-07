using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Tests.Infrastructure.AI;

public sealed class ChatGptRuntimeOptionsTests
{
    [Fact]
    public async Task Authorization_expiry_uses_configured_lifetime()
    {
        using var app = new ChatGptTestApp(configure: options => options.AuthorizationLifetime = TimeSpan.FromSeconds(1));
        await app.BeginAsync();
        app.Clock.Advance(TimeSpan.FromSeconds(2));
        using var response = await app.Client.GetAsync(app.CallbackUrl(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Server.ExchangeCount);
    }

    [Fact]
    public async Task Revocation_uses_configured_attempt_count_and_still_clears_local_session()
    {
        using var app = new ChatGptTestApp(configure: options =>
        {
            options.RevocationAttempts = 3;
            options.RevocationRetryDelay = TimeSpan.FromMilliseconds(1);
        });
        await app.ConnectAsync();
        app.Server.RevokeStatus = HttpStatusCode.ServiceUnavailable;
        var connection = app.Services.GetRequiredService<ChatGptConnection>();
        var result = await connection.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.False(result.RemoteRevocationConfirmed);
        Assert.Equal(3, app.Server.RevokeCount);
        Assert.False((await connection.GetStatusAsync(TestContext.Current.CancellationToken)).Connected);
    }

    [Fact]
    public async Task Configured_stream_limit_rejects_oversized_completed_output()
    {
        using var app = new ChatGptTestApp(configure: options => options.StreamResponseCharacters = 1024);
        await app.ConnectAsync();
        await app.SelectModelAsync();
        app.Server.StreamBody = app.Server.StreamBody.Replace("private-output", new string('s', 1025));
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateTextAsync(
            new AiTextRequest("Synthetic instruction", "Synthetic input"), TestContext.Current.CancellationToken));
        Assert.Equal(AiFailure.MalformedResponse, exception.Failure);
        Assert.DoesNotContain(new string('s', 100), app.Logs.Text);
    }
}
