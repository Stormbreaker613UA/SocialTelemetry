using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using Scenario = SocialTelemetry.Application.Features.LocalAccess.Recover.Scenario;

namespace SocialTelemetry.Api.Features.LocalAccess.Recover;

public sealed record Request(string Login, string RecoveryCode, string NewPassword);
public sealed class Endpoint(Scenario scenario, LocalAccessTransport transport) : Endpoint<Request, AccessResponse>
{
    public override void Configure() { Post("/local-access/recover"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var result = await scenario.ExecuteAsync(new(request.Login, request.RecoveryCode, request.NewPassword), cancellationToken);
        var response = await AccessHttp.CompleteAsync(HttpContext, result, transport);
        await Send.OkAsync(response, HttpContext.RequestAborted);
    }
}
