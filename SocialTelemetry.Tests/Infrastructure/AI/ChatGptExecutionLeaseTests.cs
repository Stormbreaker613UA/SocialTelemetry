using SocialTelemetry.Application.Contracts.AI;
using Microsoft.Extensions.DependencyInjection;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Application.Contracts.AI.Models;

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
        var client = scope.ServiceProvider.GetRequiredService<IAiClient>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var completed = await client.GenerateTextAsync(new AiTextRequest("Instructions", "Synthetic input"), deadline.Token);
        var binding = Assert.IsType<AiExecutionBinding>(completed.Binding);
        var connection = app.Services.GetRequiredService<ChatGptConnection>();
        var waitingConnection = (otherHost ?? app).Services.GetRequiredService<ChatGptConnection>();
        // Isolate the protected-file lease boundary: the runtime adapter additionally requires
        // its own SQL transaction, which would serialize SQLite before reaching this file lock.
        Task<IAiExecutionLease> AcquireAsync(ChatGptConnection target, CancellationToken token) =>
            target.AcquireOwnedLeaseAsync(binding.UserProfileId, binding.ConnectionId, completed.ExecutionId,
                completed.LifetimeCancellationToken, token);
        using (var lease = await AcquireAsync(connection, deadline.Token))
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var waiting = AcquireAsync(waitingConnection, caller.Token);
            Assert.False(waiting.IsCompleted);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            await lease.ValidateAsync(deadline.Token);
        }
        // Both semaphore and file-lock cancellation paths release their acquisition resources.
        using (var nextLease = await AcquireAsync(waitingConnection, deadline.Token))
            await nextLease.ValidateAsync(deadline.Token);
        Assert.True((await app.OwnedConnection.GetStatusAsync(deadline.Token)).Connected);
        await app.OwnedConnection.DisconnectAsync(deadline.Token);
    }
}
