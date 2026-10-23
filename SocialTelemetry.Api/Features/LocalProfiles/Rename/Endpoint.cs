using FastEndpoints;
using SocialTelemetry.Application.Features.LocalProfiles;
using Scenario = SocialTelemetry.Application.Features.LocalProfiles.Rename.Scenario;

namespace SocialTelemetry.Api.Features.LocalProfiles.Rename;

public sealed record Request(Guid Id, string DisplayName, Guid ExpectedVersion);

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, RenamedProfile>
{
    public override void Configure() { Patch("/local-profiles/{id:guid}/name"); AllowAnonymous(); }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var result = await scenario.ExecuteAsync(request.Id, request.DisplayName, request.ExpectedVersion, cancellationToken);
        await Send.OkAsync(result, cancellationToken);
    }
}
