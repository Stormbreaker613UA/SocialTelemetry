using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.AiConnection.Models.Scenario;

namespace SocialTelemetry.Api.Features.AiConnection.Models;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<ModelCatalogResponse>
{
    public override void Configure()
    {
        Get("/ai-connection/chatgpt/models");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(ModelCatalogResponse.From(await scenario.ExecuteAsync(cancellationToken)), cancellationToken);
}
