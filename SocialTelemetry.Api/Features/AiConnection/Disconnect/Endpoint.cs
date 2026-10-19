using FastEndpoints;
using SocialTelemetry.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.Disconnect;

public sealed class Endpoint(ProfileAiAccess connection) : EndpointWithoutRequest<ChatGptDisconnectResult>
{
    public override void Configure()
    {
        Delete("/ai-connection/chatgpt");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(await connection.DisconnectAsync(cancellationToken), cancellationToken);
}
