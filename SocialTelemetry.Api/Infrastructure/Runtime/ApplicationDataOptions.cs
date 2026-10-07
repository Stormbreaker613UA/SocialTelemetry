namespace SocialTelemetry.Api.Infrastructure.Runtime;

public sealed class ApplicationDataOptions
{
    // An unset root preserves development paths; a host may select a durable absolute root.
    public string? RootDirectory { get; set; }
}
