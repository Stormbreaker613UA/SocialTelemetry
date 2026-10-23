using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Exceptions;
using Scenario = SocialTelemetry.Application.Features.PersonGroups.Rename.Scenario;

namespace SocialTelemetry.Api.Features.PersonGroups.Rename;

public sealed record Request(Guid Id, string? Name);


public sealed class Endpoint(Scenario scenario) : Endpoint<Request, GroupResponse>
{
    public override void Configure()
    {
        Patch("/person-groups/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
        try
        {
            var group = await scenario.ExecuteAsync(request.Id, request.Name, cancellationToken);
            await Send.OkAsync(GroupResponse.From(group), transport);
        }
        catch (FeatureValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(400, cancellationToken);
        }
    }
}
