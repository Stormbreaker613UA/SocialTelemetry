using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.Unassigned;

public sealed class Endpoint(ProfileAiAccess access) : EndpointWithoutRequest<IReadOnlyList<ChatGptAccountSummary>>
{
    public override void Configure() { Get("/ai-connection/chatgpt/unassigned"); AllowAnonymous(); }
    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(await access.UnassignedAsync(cancellationToken), cancellationToken);
}
