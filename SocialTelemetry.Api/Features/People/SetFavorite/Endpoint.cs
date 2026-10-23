using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.SetFavorite;

namespace SocialTelemetry.Api.Features.People.SetFavorite;

public sealed record Request(Guid Id, bool? IsFavorite);

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Result>
{
    public override void Configure() { Put("/people/{id}/favorite"); AllowAnonymous(); }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
        try
        {
            var result = await scenario.ExecuteAsync(request.Id, request.IsFavorite, cancellationToken);
            await Send.OkAsync(result, transport);
        }
        catch (FeatureValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(400, cancellationToken);
        }
    }
}
