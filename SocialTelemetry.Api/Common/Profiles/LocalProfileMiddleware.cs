using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Features.ProfileAccess;
using SocialTelemetry.Api.Features.AiConnection;
using ResolveProfile = SocialTelemetry.Application.Features.ProfileAccess.Resolve.Scenario;
using AuthorizeRoot = SocialTelemetry.Application.Features.ProfileAccess.AuthorizeRoot.Scenario;

namespace SocialTelemetry.Api.Common.Profiles;

public sealed record LocalProfileRequestTransport(CancellationToken OriginalCancellation);

public sealed class LocalProfileMiddleware(RequestDelegate next)
{
    public const string OwnerHeader = "X-SocialTelemetry-ProfileId";
    public const string VersionHeader = "X-SocialTelemetry-ProfileVersion";

    public static bool IsOwnedPath(PathString path) => path.StartsWithSegments("/user-profile") ||
        IsLifecyclePath(path) ||
        path == "/local-access/protection" || path == "/local-access/password" ||
        path == "/local-access/recovery-code" || path == "/local-access/activity" ||
        path.StartsWithSegments("/person-groups") || path.StartsWithSegments("/people") || path.StartsWithSegments("/interactions") ||
        (path.StartsWithSegments("/ai-connection") && path != AiConnectionRoutes.CallbackPath);

    internal static bool IsLifecyclePath(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments.Length is 2 or 3 && segments[0].Equals("local-profiles", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(segments[1], out _) && (segments.Length == 2 || segments[2].Equals("name", StringComparison.OrdinalIgnoreCase));
    }

    public async Task InvokeAsync(HttpContext context, ResolveProfile resolveProfile,
        AuthorizeRoot authorizeRoot, IHostApplicationLifetime host)
    {
        var owned = IsOwnedPath(context.Request.Path);
        if (!owned && !context.Request.Path.StartsWithSegments("/local-profiles") && context.Request.Path != AiConnectionRoutes.CallbackPath)
        { await next(context); return; }
        LocalProfileBinding? binding = null;
        CancellationToken lifetime = default;
        if (owned)
        {
            var resolved = await resolveProfile.ExecuteAsync(ReadAssertion(context, OwnerHeader), ReadAssertion(context, VersionHeader), context.RequestAborted);
            binding = resolved.Binding;
            lifetime = resolved.Lifetime;
        }
        var originalCancellation = context.RequestAborted;
        context.Features.Set(new LocalProfileRequestTransport(originalCancellation));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            new[] { originalCancellation, lifetime, host.ApplicationStopping });
        context.RequestAborted = linked.Token;
        if (binding is not null)
        {
            context.Response.Headers[OwnerHeader] = binding.UserProfileId.ToString();
            context.Response.Headers[VersionHeader] = binding.SelectionVersion.ToString();
        }
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (binding is not null) await CheckRootAsync(context, authorizeRoot, linked.Token);
            await next(context);
        }
        catch (OperationCanceledException) when (!originalCancellation.IsCancellationRequested &&
            (lifetime.IsCancellationRequested || host.ApplicationStopping.IsCancellationRequested))
        {
            // Restore the real transport token so a cancellation response can still be written.
            context.RequestAborted = originalCancellation;
            throw new LocalProfileAccessException(LocalProfileFailure.SelectionChanged);
        }
        finally { context.RequestAborted = originalCancellation; }
    }

    private static Guid? ReadAssertion(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count == 0) return null;
        if (values.Count != 1 || !Guid.TryParse(values[0], out var parsed) || parsed == Guid.Empty)
            throw new LocalProfileAccessException(LocalProfileFailure.InvalidAssertion);
        return parsed;
    }

    private static async Task CheckRootAsync(HttpContext context, AuthorizeRoot authorizeRoot, CancellationToken cancellationToken)
    {
        // HTTP decodes only the route. Application authorizes the immutable owner/root pair;
        // each slice retains its existing child/parent/source checks.
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Length < 2 || !Guid.TryParse(segments[1], out var id)) return;
        var segment = segments[0].ToLowerInvariant();
        OwnershipRoot? root = null;
        if (segment == "person-groups") root = OwnershipRoot.PersonGroup;
        else if (segment == "people") root = OwnershipRoot.Person;
        else if (segment == "interactions") root = OwnershipRoot.Interaction;
        if (root is { } ownedRoot) await authorizeRoot.ExecuteAsync(ownedRoot, id, cancellationToken);
    }
}
