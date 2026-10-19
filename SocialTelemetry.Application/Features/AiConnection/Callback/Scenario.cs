namespace SocialTelemetry.Application.Features.AiConnection.Callback;

public sealed class Scenario(IProfileAiManagement connection)
{
    public Task<ConnectionStatus> ExecuteAsync(string? state, string? code, string? clientId, string? error, CancellationToken cancellationToken) =>
        connection.CompleteAsync(state, code, clientId, error, cancellationToken);
}
