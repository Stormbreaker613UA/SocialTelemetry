using Microsoft.Extensions.Options;

namespace SocialTelemetry.Infrastructure.Identity;

public sealed class LocalAccessExpirationService(IServiceScopeFactory scopes, TimeProvider clock,
    IOptions<LocalAccessOptions> options, ILogger<LocalAccessExpirationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.ExpirationCheckSeconds), clock, stoppingToken);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LocalAccessManagement>().ExpireAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Request and commit checks remain fail-closed if maintenance cannot run.
                logger.LogWarning("Local access expiration check failed with {ExceptionType}", exception.GetType().Name);
            }
        }
    }
}
