using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.SelectConnection;

public sealed record Request(Guid ConnectionId, bool AdoptUnassigned = false);
public sealed class Endpoint(ProfileAiAccess access) : Endpoint<Request>
{
    public override void Configure() { Put("/ai-connection/chatgpt/connection"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        await access.SelectConnectionAsync(request.ConnectionId, request.AdoptUnassigned, cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
