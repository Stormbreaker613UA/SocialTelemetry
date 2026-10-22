using FastEndpoints;
using Scenario = SocialTelemetry.Application.Features.LocalProfiles.Get.Scenario;

namespace SocialTelemetry.Api.Features.LocalProfiles.Get;

public sealed record ProfileIdentity(Guid Id, string DisplayName, bool AccountLinked)
{
    public string Source => "Local";
    public string Protection { get; init; } = "Unprotected";
}
public sealed record Response(Guid? UserProfileId, Guid Version, IReadOnlyList<ProfileIdentity> Profiles)
{
    public string Mode => "Local";
}
public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<Response>
{
    public override void Configure() { Get("/local-profiles"); AllowAnonymous(); }
    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var result = await scenario.ExecuteAsync(cancellationToken);
        var selection = result.Selection;
        var profiles = result.Profiles.Select(profile => new ProfileIdentity(profile.Id, profile.DisplayName, profile.AccountLinked)
            { Protection = profile.Protection }).ToArray();
        await Send.OkAsync(new Response(selection.UserProfileId, selection.Version, profiles), cancellationToken);
    }
}
