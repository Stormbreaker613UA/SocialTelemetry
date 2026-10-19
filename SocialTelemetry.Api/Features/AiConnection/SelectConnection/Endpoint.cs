using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.AiConnection.SelectConnection.Scenario;

namespace SocialTelemetry.Api.Features.AiConnection.SelectConnection;

public sealed record Request(Guid ConnectionId, bool AdoptUnassigned = false);
public sealed class Endpoint(Scenario scenario) : Endpoint<Request>
{
    public override void Configure() { Put("/ai-connection/chatgpt/connection"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        await scenario.ExecuteAsync(request.ConnectionId, request.AdoptUnassigned, cancellationToken);
        await Send.NoContentAsync(cancellationToken);
    }
}
