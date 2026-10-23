using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Exceptions;
using Scenario = SocialTelemetry.Application.Features.PersonGroups.Create.Scenario;

namespace SocialTelemetry.Api.Features.PersonGroups.Create;

public sealed record Request(string? Name);


public sealed class Endpoint(Scenario scenario) : Endpoint<Request, GroupResponse>
{
    public override void Configure()
    {
        Post("/person-groups");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
        try
        {
            var group = await scenario.ExecuteAsync(request.Name, cancellationToken);
            await Send.ResponseAsync(GroupResponse.From(group), 201, transport);
        }
        catch (FeatureValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(400, cancellationToken);
        }
    }
}
