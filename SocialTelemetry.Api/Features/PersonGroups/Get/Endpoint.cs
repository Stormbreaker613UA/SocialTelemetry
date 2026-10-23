using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Exceptions;
using Scenario = SocialTelemetry.Application.Features.PersonGroups.Get.Scenario;

namespace SocialTelemetry.Api.Features.PersonGroups.Get;

public sealed record Request(Guid Id);


public sealed class Endpoint(Scenario scenario) : Endpoint<Request, GroupResponse>
{
    public override void Configure()
    {
        Get("/person-groups/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var group = await scenario.ExecuteAsync(request.Id, cancellationToken);
        await Send.OkAsync(GroupResponse.From(group), cancellationToken);
    }
}
