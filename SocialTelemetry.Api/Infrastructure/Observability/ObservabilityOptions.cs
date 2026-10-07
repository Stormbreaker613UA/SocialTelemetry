namespace SocialTelemetry.Api.Infrastructure.Observability;

public sealed class ObservabilityOptions
{
    public bool OtlpEnabled { get; set; }
    public string? OtlpEndpoint { get; set; }

    public bool IsValid() => !OtlpEnabled ||
        (Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out var endpoint) &&
         endpoint.Scheme is "http" or "https" && string.IsNullOrEmpty(endpoint.UserInfo) &&
         string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment));
}
