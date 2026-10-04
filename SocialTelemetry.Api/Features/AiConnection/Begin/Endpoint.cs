using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.Begin;

public sealed record Request(Guid? ConnectionId = null, bool NewAccount = false, bool RequestConsent = false);
public sealed record Response(bool BrowserOpened, string Message);

public sealed class Endpoint(ChatGptConnection connection) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/ai-connection/chatgpt/connect");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        await connection.BeginAsync(request.ConnectionId, request.NewAccount, request.RequestConsent, cancellationToken);
        await Send.OkAsync(new Response(true, "Continue with ChatGPT in your system browser."), cancellationToken);
    }
}
