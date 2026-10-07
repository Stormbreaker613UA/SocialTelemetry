using System.ComponentModel.DataAnnotations;

namespace SocialTelemetry.Api.Infrastructure.Storage;

public sealed class StorageMaintenanceOptions
{
    [Range(typeof(TimeSpan), "00:00:01", "30.00:00:00")]
    public TimeSpan OrphanSafetyAge { get; set; } = TimeSpan.FromHours(1);
}
