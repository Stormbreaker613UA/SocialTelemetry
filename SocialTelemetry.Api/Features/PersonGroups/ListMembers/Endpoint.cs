using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Exceptions;
using Scenario = SocialTelemetry.Application.Features.PersonGroups.ListMembers.Scenario;

namespace SocialTelemetry.Api.Features.PersonGroups.ListMembers;

public sealed record Request(Guid Id, bool? Archived);
public sealed record Response(IReadOnlyList<SocialTelemetry.Application.Features.PersonGroups.GroupMember> Members);

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/person-groups/{id}/members");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var members = await scenario.ExecuteAsync(request.Id, request.Archived, cancellationToken);
        await Send.OkAsync(new Response(members), cancellationToken);
    }
}
