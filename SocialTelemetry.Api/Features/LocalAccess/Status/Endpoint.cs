using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using Scenario = SocialTelemetry.Application.Features.LocalAccess.Status.Scenario;

namespace SocialTelemetry.Api.Features.LocalAccess.Status;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<SocialTelemetry.Application.Features.LocalAccess.AccessState>
{
    public override void Configure() { Get("/local-access"); AllowAnonymous(); }
    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        await Send.OkAsync(await scenario.ExecuteAsync(cancellationToken), cancellationToken);
    }
}
