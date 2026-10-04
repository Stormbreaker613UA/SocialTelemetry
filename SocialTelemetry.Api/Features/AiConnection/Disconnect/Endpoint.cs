using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.Disconnect;

public sealed class Endpoint(ChatGptConnection connection) : EndpointWithoutRequest<ChatGptDisconnectResult>
{
    public override void Configure()
    {
        Delete("/ai-connection/chatgpt");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(await connection.DisconnectAsync(cancellationToken), cancellationToken);
}
