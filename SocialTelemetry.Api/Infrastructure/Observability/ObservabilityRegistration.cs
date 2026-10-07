using System.Reflection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SocialTelemetry.Api.Infrastructure.Observability;

public static class ObservabilityRegistration
{
    public static void AddSocialTelemetryDiagnostics(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<ObservabilityOptions>().BindConfiguration("Observability")
            .Validate(options => options.IsValid(), "Enabled OTLP requires an HTTP(S) endpoint without credentials, query, or fragment.")
            .ValidateOnStart();
        var options = configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new();
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(SocialTelemetryTelemetry.Name, serviceVersion: version)
                .AddAttributes([new("deployment.environment.name", environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing.AddSource(SocialTelemetryTelemetry.Name, "Npgsql")
                    .AddAspNetCoreInstrumentation(http => http.RecordException = false)
                    .AddHttpClientInstrumentation(http => http.RecordException = false)
                    .AddProcessor(new PrivacyTraceProcessor());
                if (options.OtlpEnabled && options.IsValid())
                    tracing.AddOtlpExporter(exporter => ConfigureExporter(exporter, options));
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(SocialTelemetryTelemetry.Name)
                    .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation()
                    // Drop remote host/address and other arbitrary HTTP dimensions; retain route templates and outcomes.
                    .AddView("http.*", new MetricStreamConfiguration
                    {
                        TagKeys = ["http.request.method", "http.response.status_code", "http.route", "error.type", "url.scheme"]
                    });
                if (options.OtlpEnabled && options.IsValid())
                    metrics.AddOtlpExporter(exporter => ConfigureExporter(exporter, options));
            });
    }

    private static void ConfigureExporter(OtlpExporterOptions exporter, ObservabilityOptions options)
    {
        exporter.Endpoint = new Uri(options.OtlpEndpoint ?? throw new InvalidOperationException("OTLP endpoint is required."));
        exporter.Protocol = OtlpExportProtocol.Grpc;
        exporter.Headers = null;
    }
}
