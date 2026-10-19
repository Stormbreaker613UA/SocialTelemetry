using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.AiConnection.Unassigned.Scenario;

namespace SocialTelemetry.Api.Features.AiConnection.Unassigned;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<IReadOnlyList<AccountResponse>>
{
    public override void Configure() { Get("/ai-connection/chatgpt/unassigned"); AllowAnonymous(); }
    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync((await scenario.ExecuteAsync(cancellationToken)).Select(account => new AccountResponse(account.Id, account.Label, account.Connected)).ToArray(), cancellationToken);
}
