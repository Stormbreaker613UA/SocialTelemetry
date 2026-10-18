using FastEndpoints;
using SocialTelemetry.Api.Infrastructure.Profiles;

namespace SocialTelemetry.Api.Features.LocalProfiles.Create;

public sealed record Request(string DisplayName, Guid ExpectedVersion);
public sealed record Response(Guid Id);
public sealed class Endpoint(LocalProfileSelectionService selector) : Endpoint<Request, Response>
{
    public override void Configure() { Post("/local-profiles"); AllowAnonymous(); }
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var profile = await selector.CreateAsync(request.DisplayName, request.ExpectedVersion, cancellationToken);
        await Send.ResponseAsync(new Response(profile.Id), 201, cancellationToken);
    }
}
