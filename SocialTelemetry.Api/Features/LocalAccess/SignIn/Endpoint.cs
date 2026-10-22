using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using Scenario = SocialTelemetry.Application.Features.LocalAccess.SignIn.Scenario;

namespace SocialTelemetry.Api.Features.LocalAccess.SignIn;

public sealed record Request(string Login, string Password, Guid UserProfileId, Guid ExpectedVersion, bool Remember);
public sealed class Endpoint(Scenario scenario, LocalAccessTransport transport) : Endpoint<Request, AccessResponse>
{
    public override void Configure() { Post("/local-access/sign-in"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var result = await scenario.ExecuteAsync(new(request.Login, request.Password, request.UserProfileId, request.ExpectedVersion, request.Remember), cancellationToken);
        var response = await AccessHttp.CompleteAsync(HttpContext, result, transport);
        await Send.OkAsync(response, HttpContext.RequestAborted);
    }
}
