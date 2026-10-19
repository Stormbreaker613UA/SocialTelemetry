using FastEndpoints;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Application.Features.PersonFacts.GetForPerson;

namespace SocialTelemetry.Api.Features.PersonFacts.GetForPerson;

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/people/{personId}/facts");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            var facts = await scenario.ExecuteAsync(request.PersonId, cancellationToken);
            await Send.OkAsync(new Response(facts.Select(fact => new PersonFactResponse(
                fact.Id, fact.Value, fact.Source, fact.CreatedAt)).ToList()), cancellationToken);
        }
        catch (NotFoundException)
        {
            await Send.NotFoundAsync(cancellationToken);
        }
    }
}
