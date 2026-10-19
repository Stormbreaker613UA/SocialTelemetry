using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.AiConnection.Status.Scenario;

namespace SocialTelemetry.Api.Features.AiConnection.Status;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<ConnectionResponse>
{
    public override void Configure()
    {
        Get("/ai-connection/chatgpt/status");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(ConnectionResponse.From(await scenario.ExecuteAsync(cancellationToken)), cancellationToken);
}
