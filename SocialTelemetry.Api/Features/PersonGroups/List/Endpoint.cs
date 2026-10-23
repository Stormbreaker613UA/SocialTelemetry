using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Exceptions;
using Scenario = SocialTelemetry.Application.Features.PersonGroups.List.Scenario;

namespace SocialTelemetry.Api.Features.PersonGroups.List;


public sealed record Response(IReadOnlyList<GroupResponse> Groups);

public sealed class Endpoint(Scenario scenario) : EndpointWithoutRequest<Response>
{
    public override void Configure()
    {
        Get("/person-groups");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var groups = await scenario.ExecuteAsync(cancellationToken);
        await Send.OkAsync(new Response(groups.Select(GroupResponse.From).ToList()), cancellationToken);
    }
}
