using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using SocialTelemetry.Domain.People;
using JournalProfile = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.UserProfile.Update;

public sealed class Scenario(IUserProfilePersistence persistence, LocalProfileOperation operation)
{
    public async Task<JournalProfile> ExecuteAsync(Input request, CancellationToken cancellationToken)
    {
        var ownerId = operation.RequireOwner();
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new FeatureValidationException("DisplayName is required.");
        }

        var userProfile = await persistence.FindProfileAsync(ownerId, false, cancellationToken);

        if (userProfile is null)
        {
            throw new NotFoundException();
        }

        userProfile.DisplayName = request.DisplayName.Trim();
        userProfile.AboutMe = request.AboutMe;
        userProfile.CommunicationStyle = request.CommunicationStyle;
        userProfile.Goals = request.Goals;
        userProfile.Preferences = request.Preferences;
        userProfile.Boundaries = request.Boundaries;
        userProfile.AiInstructions = request.AiInstructions;

        await persistence.SaveProfileAsync(userProfile, cancellationToken);

        return userProfile;
    }
}
