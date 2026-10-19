using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;

namespace SocialTelemetry.Application.Features.ProfileAccess.AuthorizeRoot;

public sealed class Scenario(ILocalProfileAccess access, LocalProfileOperation operation)
{
    public async Task ExecuteAsync(OwnershipRoot root, Guid id, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        if (!await access.RootExistsAsync(owner, root, id, cancellationToken))
            throw new NotFoundException();
    }
}
