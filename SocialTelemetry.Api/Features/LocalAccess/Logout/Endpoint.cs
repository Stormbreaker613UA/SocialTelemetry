using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using Scenario = SocialTelemetry.Application.Features.LocalAccess.Logout.Scenario;

namespace SocialTelemetry.Api.Features.LocalAccess.Logout;

public sealed record Request(Guid UserProfileId);
public sealed class Endpoint(Scenario scenario, LocalAccessTransport transport) : Endpoint<Request, AccessResponse>
{
    public override void Configure() { Post("/local-access/logout"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var result = await scenario.ExecuteAsync(request.UserProfileId, cancellationToken);
        var response = await AccessHttp.CompleteAsync(HttpContext, result, transport);
        await Send.OkAsync(response, HttpContext.RequestAborted);
    }
}
