using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Features.UserProfile;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class UserProfilePersistence(AppDbContext database) : IUserProfilePersistence
{
    public Task<JournalProfile?> FindProfileAsync(Guid ownerId, bool readOnly, CancellationToken cancellationToken)
    {
        var profiles = readOnly ? database.UserProfiles.AsNoTracking() : database.UserProfiles;
        return profiles.FirstOrDefaultAsync(profile => profile.Id == ownerId, cancellationToken);
    }

    public async Task SaveProfileAsync(JournalProfile profile, CancellationToken cancellationToken) =>
        await database.SaveChangesAsync(cancellationToken);
}

