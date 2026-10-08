using System.ComponentModel.DataAnnotations;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class MediaBindingOptions
{
    [Range(typeof(TimeSpan), "00:00:00.1", "00:01:00")]
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);
    [Range(typeof(TimeSpan), "00:00:00.001", "00:00:01")]
    public TimeSpan LockRetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);
    // Maintenance is explicit and offline; these values are never inferred from a missing marker.
    public string? Action { get; set; }
    public Guid? StoreId { get; set; }
    public string? Locator { get; set; }
    public Guid? PreviousStoreId { get; set; }
    public string? PreviousLocator { get; set; }
}
