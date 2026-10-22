using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using Scenario = SocialTelemetry.Application.Features.LocalAccess.Disable.Scenario;

namespace SocialTelemetry.Api.Features.LocalAccess.Disable;

public sealed record Request(string Password);
public sealed class Endpoint(Scenario scenario, LocalAccessTransport transport) : Endpoint<Request, AccessResponse>
{
    public override void Configure() { Delete("/local-access/protection"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var result = await scenario.ExecuteAsync(request.Password, cancellationToken);
        var response = await AccessHttp.CompleteAsync(HttpContext, result, transport);
        await Send.OkAsync(response, HttpContext.RequestAborted);
    }
}
