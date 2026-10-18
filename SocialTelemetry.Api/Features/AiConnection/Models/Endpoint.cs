using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.Models;

public sealed class Endpoint(ProfileAiAccess connection) : EndpointWithoutRequest<ChatGptModelCatalog>
{
    public override void Configure()
    {
        Get("/ai-connection/chatgpt/models");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(await connection.ModelsAsync(cancellationToken), cancellationToken);
}
