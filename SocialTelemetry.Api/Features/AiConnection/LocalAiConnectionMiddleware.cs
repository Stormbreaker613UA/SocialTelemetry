using System.Net;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Features.AiConnection;

public sealed class LocalAiConnectionMiddleware(RequestDelegate next, IOptions<ChatGptOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var remoteAddress = context.Connection.RemoteIpAddress;
        var host = context.Request.Host.Host;
        var localHost = host == "localhost" || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress) || !localHost)
            throw new AiProviderException(AiFailure.LocalRequestRequired);

        if (context.Request.Path == ChatGptOptions.CallbackPath)
        {
            var callback = new Uri(options.Value.CallbackUri);
            if (context.Request.Scheme != callback.Scheme || context.Request.Host.Value != callback.Authority)
                throw new AiProviderException(AiFailure.InvalidCallback);
        }
        else
        {
            // A custom header prevents a third-party website from changing this local connection.
            var origin = context.Request.Headers.Origin.ToString();
            var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
            if (context.Request.Headers["X-SocialTelemetry-Local"] != "1" ||
                (origin.Length != 0 && !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase)) ||
                context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                throw new AiProviderException(AiFailure.LocalRequestRequired);
        }
        await next(context);
    }
}
