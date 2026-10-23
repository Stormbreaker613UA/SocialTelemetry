using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Api.Features.PersonGroups;

public sealed record GroupResponse(Guid Id, Guid UserProfileId, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static GroupResponse From(PersonGroup group) => new(group.Id, group.UserProfileId, group.Name, group.CreatedAt, group.UpdatedAt);
}
