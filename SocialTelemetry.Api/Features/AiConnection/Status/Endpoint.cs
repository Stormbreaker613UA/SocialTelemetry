using FastEndpoints;
using SocialTelemetry.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.Status;

public sealed class Endpoint(ProfileAiAccess connection) : EndpointWithoutRequest<ChatGptConnectionStatus>
{
    public override void Configure()
    {
        Get("/ai-connection/chatgpt/status");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(await connection.StatusAsync(cancellationToken), cancellationToken);
}
