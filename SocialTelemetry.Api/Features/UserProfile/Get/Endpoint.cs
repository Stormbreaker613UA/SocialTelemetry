using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.Get;

public sealed class Endpoint(AppDbContext dbContext, LocalProfileOperation operation) : EndpointWithoutRequest<Response>
{
    public override void Configure()
    {
        Get("/user-profile");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var userProfile = await dbContext.UserProfiles.Where(profile => profile.Id == operation.RequireOwner())
            .AsNoTracking()
            .Select(userProfile => new Response(
                userProfile.Id,
                userProfile.DisplayName,
                userProfile.AboutMe,
                userProfile.CommunicationStyle,
                userProfile.Goals,
                userProfile.Preferences,
                userProfile.Boundaries,
                userProfile.AiInstructions))
            .FirstOrDefaultAsync(cancellationToken);

        if (userProfile is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        await Send.OkAsync(userProfile, cancellationToken);
    }
}
