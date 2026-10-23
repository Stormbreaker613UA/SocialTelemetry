using FastEndpoints;
using SocialTelemetry.Api.Common.Profiles;
using SocialTelemetry.Application.Features.LocalProfiles;
using Scenario = SocialTelemetry.Application.Features.LocalProfiles.Delete.Scenario;

namespace SocialTelemetry.Api.Features.LocalProfiles.Delete;

public sealed class Request
{
    public Guid Id { get; init; }
    public Guid ExpectedVersion { get; init; }
    public bool ConfirmJournalDeletion { get; init; }
    public string? CurrentPassword { get; init; }
}

public sealed class Endpoint(Scenario scenario) : Endpoint<Request, DeletedProfile>
{
    public override void Configure() { Delete("/local-profiles/{id:guid}"); AllowAnonymous(); }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var result = await scenario.ExecuteAsync(request.Id, request.ExpectedVersion, request.ConfirmJournalDeletion,
            request.CurrentPassword, cancellationToken);
        // Deletion publishes owner cancellation after commit. Use the real transport token for its response.
        var transport = HttpContext.Features.Get<LocalProfileRequestTransport>()?.OriginalCancellation ?? cancellationToken;
        await Send.OkAsync(result, transport);
    }
}
