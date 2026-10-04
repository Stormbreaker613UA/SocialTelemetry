using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.Callback;

public sealed class Endpoint(ChatGptConnection connection) : EndpointWithoutRequest<ChatGptConnectionStatus>
{
    public override void Configure()
    {
        Get(ChatGptOptions.CallbackPath);
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var status = await connection.CompleteAsync(ReadParameter("state"), ReadParameter("code"),
            ReadParameter("client_id"), ReadParameter("error"), cancellationToken);
        await Send.OkAsync(status, cancellationToken);
    }

    private string? ReadParameter(string name)
    {
        var values = HttpContext.Request.Query[name];
        if (values.Count > 1) throw new AiProviderException(AiFailure.InvalidCallback);
        return values.Count == 0 ? null : values[0];
    }
}
