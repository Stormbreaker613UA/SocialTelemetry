using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.LocalProfiles.Create.Scenario;

namespace SocialTelemetry.Api.Features.LocalProfiles.Create;

public sealed record Request(string DisplayName, Guid ExpectedVersion);
public sealed record Response(Guid Id);
public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure() { Post("/local-profiles"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var profile = await scenario.ExecuteAsync(request.DisplayName, request.ExpectedVersion, cancellationToken);
        await Send.ResponseAsync(new Response(profile.Id), 201, cancellationToken);
    }
}
