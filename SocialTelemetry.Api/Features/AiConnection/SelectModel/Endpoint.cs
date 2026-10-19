using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.AiConnection.SelectModel.Scenario;

namespace SocialTelemetry.Api.Features.AiConnection.SelectModel;

public sealed record Request(string ModelId);
public sealed record Response(string ModelId);

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/ai-connection/chatgpt/model");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        await scenario.ExecuteAsync(request.ModelId, cancellationToken);
        await Send.OkAsync(new Response(request.ModelId), cancellationToken);
    }
}
