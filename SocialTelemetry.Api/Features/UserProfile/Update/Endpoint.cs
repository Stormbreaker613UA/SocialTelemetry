using SocialTelemetry.Application.Contracts.Profiles;
using FastEndpoints;
using SocialTelemetry.Infrastructure.Profiles;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Infrastructure.Persistence;

namespace SocialTelemetry.Api.Features.UserProfile.Update;

public sealed class Endpoint(AppDbContext dbContext, LocalProfileOperation operation) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/user-profile");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            AddError("DisplayName is required.");
            await Send.ErrorsAsync(400, cancellationToken);
            return;
        }

        var userProfile = await dbContext.UserProfiles.Where(profile => profile.Id == operation.RequireOwner())
            .FirstOrDefaultAsync(cancellationToken);

        if (userProfile is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        userProfile.DisplayName = request.DisplayName.Trim();
        userProfile.AboutMe = request.AboutMe;
        userProfile.CommunicationStyle = request.CommunicationStyle;
        userProfile.Goals = request.Goals;
        userProfile.Preferences = request.Preferences;
        userProfile.Boundaries = request.Boundaries;
        userProfile.AiInstructions = request.AiInstructions;

        await dbContext.SaveChangesAsync(cancellationToken);

        await Send.OkAsync(new Response(
            userProfile.Id,
            userProfile.DisplayName,
            userProfile.AboutMe,
            userProfile.CommunicationStyle,
            userProfile.Goals,
            userProfile.Preferences,
            userProfile.Boundaries,
            userProfile.AiInstructions), cancellationToken);
    }
}
