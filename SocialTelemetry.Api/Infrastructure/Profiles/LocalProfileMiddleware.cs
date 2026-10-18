using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Infrastructure.Profiles;

public sealed class LocalProfileMiddleware(RequestDelegate next)
{
    public const string OwnerHeader = "X-SocialTelemetry-ProfileId";
    public const string VersionHeader = "X-SocialTelemetry-ProfileVersion";

    public static bool IsOwnedPath(PathString path) => path.StartsWithSegments("/user-profile") ||
        path.StartsWithSegments("/people") || path.StartsWithSegments("/interactions") ||
        (path.StartsWithSegments("/ai-connection") && path != ChatGptOptions.CallbackPath);

    public async Task InvokeAsync(HttpContext context, LocalProfileSelectionService selector,
        LocalProfileOperation operation, LocalProfileLifetimes lifetimes, AppDbContext database, IHostApplicationLifetime host)
    {
        var owned = IsOwnedPath(context.Request.Path);
        if (!owned && !context.Request.Path.StartsWithSegments("/local-profiles") && context.Request.Path != ChatGptOptions.CallbackPath)
        { await next(context); return; }
        LocalProfileBinding? binding = null;
        CancellationToken lifetime = default;
        if (owned)
        {
            binding = await selector.ResolveAsync(ReadAssertion(context, OwnerHeader), ReadAssertion(context, VersionHeader), context.RequestAborted);
            lifetime = lifetimes.Capture(binding.SelectionVersion);
            operation.Bind(binding, lifetime);
        }
        var originalCancellation = context.RequestAborted;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(originalCancellation, lifetime, host.ApplicationStopping);
        context.RequestAborted = linked.Token;
        if (binding is not null)
        {
            context.Response.Headers[OwnerHeader] = binding.UserProfileId.ToString();
            context.Response.Headers[VersionHeader] = binding.SelectionVersion.ToString();
        }
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (binding is not null) await CheckRootAsync(context, database, binding.UserProfileId, linked.Token);
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

    private static async Task CheckRootAsync(HttpContext context, AppDbContext database, Guid owner, CancellationToken cancellationToken)
    {
        // Existing slices retain their child-id/parent-id and source checks. Authorize the
        // ownership root first, including media and child Update/Delete routes.
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Length < 2 || !Guid.TryParse(segments[1], out var id)) return;
        var owned = segments[0].ToLowerInvariant() switch
        {
            "people" => await database.People.AsNoTracking().AnyAsync(person => person.Id == id && person.UserProfileId == owner, cancellationToken),
            "interactions" => await database.Interactions.AsNoTracking().AnyAsync(interaction => interaction.Id == id && interaction.UserProfileId == owner, cancellationToken),
            _ => true
        };
        if (!owned) throw new NotFoundException();
    }
}
