using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;

namespace SocialTelemetry.Application.Features.LocalProfiles.Delete;

public sealed class Scenario(IOpenedProfileLifecycle persistence, LocalProfileOperation operation)
{
    public async Task<DeletedProfile> ExecuteAsync(Guid id, Guid expectedVersion, bool confirmJournalDeletion,
        string? currentPassword, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken);
        if (id != owner) throw new NotFoundException();
        SelectionPolicy.CheckVersion(new(owner, operation.Binding!.SelectionVersion, false), expectedVersion);
        if (!confirmJournalDeletion) throw new InvalidInputException();
        // The protected operation invalidates its own lifetime only after a successful commit.
        return await persistence.DeleteAsync(owner, expectedVersion, currentPassword, admission.CancellationToken);
    }
}
