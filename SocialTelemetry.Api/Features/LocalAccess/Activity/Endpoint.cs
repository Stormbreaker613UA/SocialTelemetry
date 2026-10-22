using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using Scenario = SocialTelemetry.Application.Features.LocalAccess.Activity.Scenario;

namespace SocialTelemetry.Api.Features.LocalAccess.Activity;

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<AccessResponse>
{
    public override void Configure() { Post("/local-access/activity"); AllowAnonymous(); }
    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        await scenario.ExecuteAsync(cancellationToken);
        AccessHttp.CompleteCommittedControl(HttpContext);
        await Send.NoContentAsync(HttpContext.RequestAborted);
    }
}
