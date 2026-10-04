using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.Status;

public sealed class Endpoint(ChatGptConnection connection) : EndpointWithoutRequest<ChatGptConnectionStatus>
{
    public override void Configure()
    {
        Get("/ai-connection/chatgpt/status");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(await connection.GetStatusAsync(cancellationToken), cancellationToken);
}
