using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;

namespace SocialTelemetry.Api.Features.LocalProfiles.Select;

public sealed record Request(Guid UserProfileId, Guid ExpectedVersion);
public sealed record Response(Guid? UserProfileId, Guid Version);
public sealed class Endpoint(LocalProfileSelectionService selector) : Endpoint<Request, Response>
{
    public override void Configure() { Put("/local-profiles/current"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var selection = await selector.SelectAsync(request.UserProfileId, request.ExpectedVersion, cancellationToken);
        await Send.OkAsync(new Response(selection.UserProfileId, selection.Version), cancellationToken);
    }
}
