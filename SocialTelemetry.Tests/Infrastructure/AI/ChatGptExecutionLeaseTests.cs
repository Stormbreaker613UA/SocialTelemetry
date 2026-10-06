using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.AI.Models;

namespace SocialTelemetry.Tests.Infrastructure.AI;

public sealed class ChatGptExecutionLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_can_cancel_waiting_for_execution_lease_without_losing_session(bool sharedHost)
    {
        using var app = new ChatGptTestApp();
        await app.ConnectAsync();
        await app.SelectModelAsync();
        using var otherHost = sharedHost ? new ChatGptTestApp(app) : null;
        await using var scope = app.Services.CreateAsyncScope();
        await using var waitingScope = (otherHost ?? app).Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        var waitingClient = waitingScope.ServiceProvider.GetRequiredService<IAiClient>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var completed = await client.GenerateTextAsync(new AiTextRequest("Instructions", "Synthetic input"), deadline.Token);
        using (var lease = await client.AcquireExecutionLeaseAsync(completed, deadline.Token))
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var waiting = waitingClient.AcquireExecutionLeaseAsync(completed, caller.Token);
            Assert.False(waiting.IsCompleted);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            await lease.ValidateAsync(deadline.Token);
        }
        // Both semaphore and file-lock cancellation paths release their acquisition resources.
        using (var nextLease = await waitingClient.AcquireExecutionLeaseAsync(completed, deadline.Token))
            await nextLease.ValidateAsync(deadline.Token);
        Assert.True((await app.Services.GetRequiredService<ChatGptConnection>().GetStatusAsync(deadline.Token)).Connected);
        await app.Services.GetRequiredService<ChatGptConnection>().DisconnectAsync(deadline.Token);
    }
}
