using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Features.LocalAccess;

namespace SocialTelemetry.Api.Features.LocalAccess;

public sealed record AccessResponse(Guid? UserProfileId, Guid Version, string? RecoveryCode);

public static class AccessHttp
{
    public static async Task<AccessResponse> CompleteAsync(HttpContext context, AccessChange result, LocalAccessTransport transport)
    {
        CompleteCommittedControl(context);
        context.Response.Headers[LocalProfileMiddleware.VersionHeader] = result.Selection.Version.ToString();
        if (result.Selection.UserProfileId is { } owner)
            context.Response.Headers[LocalProfileMiddleware.OwnerHeader] = owner.ToString();
        if (result.DeviceEvidence?.DeviceToken is { } token)
        {
            var deviceIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            var identity = new ClaimsIdentity(new[] { new Claim(transport.DeviceClaim, token),
                new Claim(ClaimTypes.NameIdentifier, deviceIdentity) }, transport.Scheme);
            await context.SignInAsync(transport.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IsPersistent = result.PersistentDevice, ExpiresUtc = result.DeviceExpiresAt, AllowRefresh = false
            });
        }
        return new AccessResponse(result.Selection.UserProfileId, result.Selection.Version, result.RecoveryCode);
    }

    public static void CompleteCommittedControl(HttpContext context)
    {
        // A successful security commit intentionally retires its old profile lifetime.
        // Preserve the actual caller cancellation while returning that committed outcome.
        if (context.Features.Get<LocalProfileRequestTransport>() is { } request)
            context.RequestAborted = request.OriginalCancellation;
    }
}
