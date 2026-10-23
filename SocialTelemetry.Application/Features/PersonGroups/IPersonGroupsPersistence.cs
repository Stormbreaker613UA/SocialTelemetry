using SocialTelemetry.Domain.People;

namespace SocialTelemetry.Application.Features.PersonGroups;

public sealed record GroupMember(Guid PersonId, string DisplayName, bool IsFavorite, DateTimeOffset? ArchivedAt);

public interface IPersonGroupsPersistence
{
    Task<IReadOnlyList<PersonGroup>> ListAsync(Guid owner, CancellationToken cancellationToken);
    Task<PersonGroup> GetAsync(Guid owner, Guid groupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GroupMember>> MembersAsync(Guid owner, Guid groupId, bool? archived, CancellationToken cancellationToken);
    Task CreateAsync(PersonGroup group, CancellationToken cancellationToken);
    Task<PersonGroup> RenameAsync(Guid owner, Guid groupId, Action<PersonGroup> rename, CancellationToken cancellationToken);
    Task DeleteAsync(Guid owner, Guid groupId, CancellationToken cancellationToken);
    Task ChangeMembershipAsync(Guid owner, Guid groupId, Guid personId, Func<bool, bool> desiredMembership, CancellationToken cancellationToken);
}
