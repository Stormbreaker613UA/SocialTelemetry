using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Exceptions;
using Scenario = SocialTelemetry.Application.Features.PersonGroups.RemoveMember.Scenario;

namespace SocialTelemetry.Api.Features.PersonGroups.RemoveMember;

public sealed record Request(Guid Id, Guid PersonId);


public sealed class Endpoint(Scenario scenario) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/person-groups/{id}/members/{personId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
        await scenario.ExecuteAsync(request.Id, request.PersonId, cancellationToken);
        await Send.NoContentAsync(transport);
    }
}
