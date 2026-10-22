using FastEndpoints;
using Microsoft.AspNetCore.Antiforgery;

namespace SocialTelemetry.Api.Features.LocalAccess.Bootstrap;

public sealed record Response(string RequestToken);
public sealed class Endpoint(IAntiforgery antiforgery) : EndpointWithoutRequest<Response>
{
    public override void Configure() { Get("/local-access/antiforgery"); AllowAnonymous(); }
    public override async Task HandleAsync(CancellationToken cancellationToken) =>
        await Send.OkAsync(new Response(antiforgery.GetAndStoreTokens(HttpContext).RequestToken!), cancellationToken);
}
