namespace SocialTelemetry.Api.Infrastructure.Persistence;

internal sealed class AnalysisContextGuard
{
    // No FK: protection must exist before inserting a new profile and survive profile deletion.
    public Guid UserProfileId { get; set; }
}
