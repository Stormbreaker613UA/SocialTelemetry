using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Api.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.Get;

public sealed class Endpoint(AppDbContext dbContext) : EndpointWithoutRequest<Response>
{
    public override void Configure()
    {
        Get("/user-profile");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        var userProfile = await dbContext.UserProfiles
            .AsNoTracking()
            .OrderBy(userProfile => userProfile.Id)
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
