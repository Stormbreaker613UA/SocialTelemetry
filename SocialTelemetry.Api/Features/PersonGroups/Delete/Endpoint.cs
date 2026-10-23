using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Exceptions;
using Scenario = SocialTelemetry.Application.Features.PersonGroups.Delete.Scenario;

namespace SocialTelemetry.Api.Features.PersonGroups.Delete;

public sealed record Request(Guid Id);


public sealed class Endpoint(Scenario scenario) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/person-groups/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
        await scenario.ExecuteAsync(request.Id, cancellationToken);
        await Send.NoContentAsync(transport);
    }
}
