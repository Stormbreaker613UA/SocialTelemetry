using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;

namespace SocialTelemetry.Application.Features.People.SetFavorite;

public sealed record Result(Guid PersonId, bool IsFavorite, DateTimeOffset UpdatedAt);

public sealed class Scenario(IPeoplePersistence persistence, LocalProfileOperation operation)
{
    public async Task<Result> ExecuteAsync(Guid personId, bool? isFavorite, CancellationToken cancellationToken)
    {
        var owner = operation.RequireOwner();
        using var admission = await operation.EnterAsync(cancellationToken, userMutation: true);
        if (isFavorite is null) throw new FeatureValidationException("IsFavorite is required.");
        var person = await persistence.SetFavoriteAsync(owner, personId, current =>
        {
            if (current.IsFavorite == isFavorite.Value) return;
            current.IsFavorite = isFavorite.Value;
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }, admission.CancellationToken);
        return new(person.Id, person.IsFavorite, person.UpdatedAt);
    }
}
