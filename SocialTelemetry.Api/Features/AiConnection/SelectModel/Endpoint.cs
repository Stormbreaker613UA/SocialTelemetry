using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection.SelectModel;

public sealed record Request(string ModelId);
public sealed record Response(string ModelId);

public sealed class Endpoint(ChatGptConnection connection) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/ai-connection/chatgpt/model");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        await connection.SelectModelAsync(request.ModelId, cancellationToken);
        await Send.OkAsync(new Response(request.ModelId), cancellationToken);
    }
}
