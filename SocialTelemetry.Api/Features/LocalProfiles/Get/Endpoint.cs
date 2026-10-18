using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Profiles;

namespace SocialTelemetry.Api.Features.LocalProfiles.Get;

public sealed record ProfileIdentity(Guid Id, string DisplayName, bool AccountLinked);
public sealed record Response(Guid? UserProfileId, Guid Version, IReadOnlyList<ProfileIdentity> Profiles);
public sealed class Endpoint(AppDbContext database, LocalProfileSelectionService selector) : EndpointWithoutRequest<Response>
{
    public override void Configure() { Get("/local-profiles"); AllowAnonymous(); }
    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var selection = await selector.GetAsync(cancellationToken);
        var profiles = await database.UserProfiles.AsNoTracking().OrderBy(profile => profile.DisplayName).ThenBy(profile => profile.Id)
            .Select(profile => new ProfileIdentity(profile.Id, profile.DisplayName, database.Users.Any(account => account.UserProfileId == profile.Id)))
            .ToListAsync(cancellationToken);
        await Send.OkAsync(new Response(selection.UserProfileId, selection.Version, profiles), cancellationToken);
    }
}
