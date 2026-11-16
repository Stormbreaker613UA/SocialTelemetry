using SocialTelemetry.Api.Common.Profiles;
using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.AiConnection.Disconnect.Scenario;

namespace SocialTelemetry.Api.Features.AiConnection.Disconnect;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<DisconnectResponse>
{
    public override void Configure()
    {
        Delete("/ai-connection/chatgpt");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var result = await scenario.ExecuteAsync(cancellationToken);
        var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
        await Send.OkAsync(new DisconnectResponse(result.RemoteRevocationConfirmed, result.Message), transport);
    }
}
