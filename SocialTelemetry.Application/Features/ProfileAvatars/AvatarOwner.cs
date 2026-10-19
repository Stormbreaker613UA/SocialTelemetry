using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.ProfileAvatars;

public sealed class AvatarOwner
{
    private readonly Person? person;
    private readonly JournalProfile? profile;

    public AvatarOwner(Person person) => this.person = person;
    public AvatarOwner(JournalProfile profile) => this.profile = profile;

    public Guid UserProfileId => person?.UserProfileId ?? profile!.Id;
    public string? StorageKey => person is not null ? person.AvatarStorageKey : profile!.AvatarStorageKey;
    public string? MimeType => person is not null ? person.AvatarMimeType : profile!.AvatarMimeType;

    public void SetAvatar(string? key, string? mimeType)
    {
        if (person is not null)
        {
            person.AvatarStorageKey = key;
            person.AvatarMimeType = mimeType;
        }
        else if (profile is not null)
        {
            profile.AvatarStorageKey = key;
            profile.AvatarMimeType = mimeType;
        }
    }
}
