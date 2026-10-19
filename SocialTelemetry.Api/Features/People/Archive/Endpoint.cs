using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.People.Archive;

namespace SocialTelemetry.Api.Features.People.Archive;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request>
{
    public override void Configure()
    {
        Put("/people/{id}/archive");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            await scenario.ExecuteAsync(new Input(request.Id), cancellationToken);
            await Send.NoContentAsync(cancellationToken);
        }
        catch (FeatureValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(400, cancellationToken);
        }
        catch (NotFoundException)
        {
            await Send.NotFoundAsync(cancellationToken);
        }
    }
}
