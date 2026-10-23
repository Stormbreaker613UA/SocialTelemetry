using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using SocialTelemetry.Application.Contracts.Profiles;

namespace SocialTelemetry.Api.Common.Profiles;

public sealed record LocalAccessTransport(string Scheme, string DeviceClaim);

public sealed class LocalAccessHttpMiddleware(RequestDelegate next, LocalAccessTransport transport)
{
    public async Task InvokeAsync(HttpContext context, LocalAccessRequest request, IAntiforgery antiforgery)
    {
        var authentication = await context.AuthenticateAsync(transport.Scheme);
        if (authentication.Principal is { } principal) context.User = principal;
        var proof = authentication.Principal?.FindFirst(transport.DeviceClaim)?.Value;
        request.SetEvidence(new LocalAccessEvidence(proof));
        var accessRoute = context.Request.Path.StartsWithSegments("/local-access");
        var protectedTransport = proof is not null && (LocalProfileMiddleware.IsOwnedPath(context.Request.Path) ||
            context.Request.Path.StartsWithSegments("/local-profiles"));
        // A password-bearing destructive body must never be accepted over insecure transport,
        // even when no device cookie was supplied. Unprotected profiles still need no account.
        var journalDeletion = HttpMethods.IsDelete(context.Request.Method) && LocalProfileMiddleware.IsLifecyclePath(context.Request.Path);
        if (accessRoute || protectedTransport || journalDeletion)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var remote = context.Connection.RemoteIpAddress;
            var host = context.Request.Host.Host;
            var loopbackHost = host == "localhost" || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
            var origin = context.Request.Headers.Origin.ToString();
            if (!context.Request.IsHttps || remote is null || !IPAddress.IsLoopback(remote) || !loopbackHost ||
                context.Request.Headers["X-SocialTelemetry-Local"] != "1" || context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
                origin.Length > 0 && !string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
                throw new LocalAccessException(LocalAccessFailure.LocalRequestRequired);
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { throw new LocalAccessException(LocalAccessFailure.LocalRequestRequired); }
            }
        }
        if (accessRoute || LocalProfileMiddleware.IsLifecyclePath(context.Request.Path))
        {
            const int bodyBytes = 8192;
            if (context.Request.ContentLength > bodyBytes) throw new BadHttpRequestException("Request too large.", 413);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maximum) maximum.MaxRequestBodySize = bodyBytes;
        }
        await next(context);
    }
}
