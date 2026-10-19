using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.UserProfile;

public interface IUserProfilePersistence
{
    Task<JournalProfile?> FindProfileAsync(Guid ownerId, bool readOnly, CancellationToken cancellationToken);
    Task SaveProfileAsync(JournalProfile profile, CancellationToken cancellationToken);
}

