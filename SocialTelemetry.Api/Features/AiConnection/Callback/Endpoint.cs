using SocialTelemetry.Application.Contracts.AI;
using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.AiConnection.Callback.Scenario;

namespace SocialTelemetry.Api.Features.AiConnection.Callback;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<ConnectionResponse>
{
    public override void Configure()
    {
        Get(AiConnectionRoutes.CallbackPath);
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var status = await scenario.ExecuteAsync(ReadParameter("state"), ReadParameter("code"),
            ReadParameter("client_id"), ReadParameter("error"), cancellationToken);
        await Send.OkAsync(ConnectionResponse.From(status), cancellationToken);
    }

    private string? ReadParameter(string name)
    {
        var values = HttpContext.Request.Query[name];
        if (values.Count > 1) throw new AiProviderException(AiFailure.InvalidCallback);
        return values.Count == 0 ? null : values[0];
    }
}
