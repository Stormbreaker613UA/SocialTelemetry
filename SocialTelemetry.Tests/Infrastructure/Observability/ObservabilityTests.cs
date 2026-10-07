using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Serilog.Core;
using Serilog.Events;
using SocialTelemetry.Api.Infrastructure.Observability;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Tests.Infrastructure.Observability;

[Collection("Observability")]
public sealed class ObservabilityTests
{
    private const string PrivateMarker = "PRIVATE_PROFILE_EVIDENCE_PROMPT_TOKEN_DATABASE_DETAIL";

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "not-an-endpoint", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "ftp://localhost:4317", false)]
    [InlineData(true, "http://user:secret@localhost:4317", false)]
    [InlineData(true, "http://localhost:4317?token=secret", false)]
    [InlineData(true, "http://localhost:4317#secret", false)]
    [InlineData(true, "https://collector.example:4317", true)]
    public void Export_configuration_requires_a_safe_endpoint_only_when_enabled(bool enabled, string? endpoint, bool valid)
    {
        Assert.Equal(valid, new ObservabilityOptions { OtlpEnabled = enabled, OtlpEndpoint = endpoint }.IsValid());
    }

    [Fact]
    public void Invalid_enabled_export_configuration_is_rejected_at_startup()
    {
        using var application = CreateApp(new() { ["Observability:OtlpEnabled"] = "true", ["Observability:OtlpEndpoint"] = "invalid" });
        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(nameof(OptionsValidationException), exception.ToString());
    }

    [Fact]
    public async Task Disabled_export_starts_without_collector_and_HTTP_telemetry_and_logs_remain_safe_and_correlated()
    {
        var traces = new CapturedTraces();
        var logs = new CapturedLogs();
        using var application = CreateApp(new() { ["Observability:OtlpEndpoint"] = "invalid" }, services =>
        {
            services.AddSingleton<ILogEventSink>(logs);
            services.ConfigureOpenTelemetryTracerProvider(builder => builder.AddProcessor(new SimpleActivityExportProcessor(traces)));
        });
        // TestServer does not emit the real ASP.NET Core server Activity; use a disposable loopback listener.
        application.UseKestrel(0);
        using var client = application.CreateClient();
        Assert.False(application.Services.GetRequiredService<IOptions<ObservabilityOptions>>().Value.OtlpEnabled);
        using var response = await client.GetAsync($"/not-a-route?code={PrivateMarker}&state={PrivateMarker}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await traces.ServerEnded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var server = Assert.Single(traces.Spans, span => span.Kind == ActivityKind.Server);
        Assert.Equal("HTTP request", server.Name);
        Assert.Equal(404, server.Tags["http.response.status_code"]);
        Assert.DoesNotContain(PrivateMarker, server.ToString());
        Assert.DoesNotContain(server.Tags.Keys, key => key.Contains("url") || key.Contains("header"));
        Assert.Contains(logs.Events, entry => entry.TraceId == server.TraceId && entry.SpanId == server.SpanId);
    }

    [Theory]
    [InlineData("success", ActivityStatusCode.Ok)]
    [InlineData("failure", ActivityStatusCode.Error)]
    [InlineData("cancelled", ActivityStatusCode.Ok)]
    public void Custom_operations_emit_safe_outcomes_without_exception_messages(string outcome, ActivityStatusCode status)
    {
        var traces = new CapturedTraces();
        using var provider = Capture(traces);
        using (var operation = SocialTelemetryTelemetry.StartAi("test-provider", "test-model", vision: true))
        {
            if (outcome == "success") operation.Complete();
            else operation.Fail(outcome == "cancelled" ? new OperationCanceledException(PrivateMarker) : new InvalidOperationException(PrivateMarker));
        }
        var span = Assert.Single(traces.Spans);
        Assert.Equal("ai.execute", span.Name);
        Assert.Equal(status, span.Status);
        Assert.Equal(outcome, span.Tags["outcome"]);
        Assert.Equal("test-provider", span.Tags["ai.provider"]);
        Assert.Equal("test-model", span.Tags["ai.requested_model"]);
        Assert.Equal("text_image", span.Tags["ai.input.kind"]);
        Assert.Empty(span.Events);
        Assert.DoesNotContain(PrivateMarker, span.ToString());
    }

    [Fact]
    public void AI_metrics_count_once_and_exclude_model_and_entity_dimensions()
    {
        var measurements = new ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == SocialTelemetryTelemetry.Name) meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.Start();
        using (var operation = SocialTelemetryTelemetry.StartAi("metric-test-provider", "opaque-model", false)) operation.Complete();
        SocialTelemetryTelemetry.RecordReconciliation("attachments", "recovered");
        var own = measurements.Where(measurement => measurement.Tags.Any(tag => Equals(tag.Value, "metric-test-provider"))).ToArray();
        Assert.Equal(2, own.Length);
        Assert.Equal(1, Assert.Single(own, measurement => measurement.Name == "socialtelemetry.ai.executions").Value);
        Assert.True(Assert.Single(own, measurement => measurement.Name == "socialtelemetry.ai.duration").Value >= 0);
        Assert.All(own, measurement => Assert.Equal(new[] { "provider", "outcome" }, measurement.Tags.Select(tag => tag.Key)));
        var maintenance = Assert.Single(measurements, measurement => measurement.Name == "socialtelemetry.storage.reconciliation.actions");
        Assert.Equal(new[] { "storage.area", "action" }, maintenance.Tags.Select(tag => tag.Key));
    }

    [Fact]
    public void Database_error_telemetry_drops_SQL_paths_credentials_and_exception_details()
    {
        var traces = new CapturedTraces();
        using var provider = Capture(traces);
        using var source = new ActivitySource("Npgsql");
        using (var activity = source.StartActivity(PrivateMarker, ActivityKind.Client))
        {
            Assert.NotNull(activity);
            activity.SetTag("db.system.name", "postgresql");
            activity.SetTag("db.query.text", $"SELECT '{PrivateMarker}'");
            activity.SetTag("db.npgsql.data_source", PrivateMarker);
            activity.SetTag("server.address", PrivateMarker);
            activity.AddException(new InvalidOperationException(PrivateMarker));
            activity.SetStatus(ActivityStatusCode.Error, PrivateMarker);
        }
        var span = Assert.Single(traces.Spans);
        Assert.Equal("database.command", span.Name);
        Assert.Equal("postgresql", Assert.Single(span.Tags).Value);
        Assert.Null(span.StatusDescription);
        Assert.DoesNotContain(PrivateMarker, span.ToString());
        Assert.Equal("InvalidOperationException", Assert.Single(span.Events).Tags["exception.type"]);
    }

    [Fact]
    public async Task Real_loopback_HttpClient_trace_does_not_export_authentication_URL_query_or_headers()
    {
        var traces = new CapturedTraces();
        using var provider = Sdk.CreateTracerProviderBuilder().AddHttpClientInstrumentation(options => options.RecordException = false)
            .AddProcessor(new PrivacyTraceProcessor()).AddProcessor(new SimpleActivityExportProcessor(traces)).Build();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cancellationToken = TestContext.Current.CancellationToken;
        var serve = ServeAsync(listener, cancellationToken);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/{PrivateMarker}?code={PrivateMarker}");
        request.Headers.Authorization = new("Bearer", PrivateMarker);
        using var response = await client.SendAsync(request, cancellationToken);
        await serve;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var span = Assert.Single(traces.Spans, span => span.Kind == ActivityKind.Client && span.Tags.ContainsKey("http.request.method"));
        Assert.Equal("HTTP client", span.Name);
        Assert.DoesNotContain(PrivateMarker, span.ToString());
        Assert.DoesNotContain(span.Tags.Keys, key => key.Contains("url") || key.Contains("header") || key.Contains("address"));
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken))) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellationToken);
    }

    private static TracerProvider Capture(CapturedTraces traces) => Sdk.CreateTracerProviderBuilder()
        .AddSource(SocialTelemetryTelemetry.Name, "Npgsql").AddProcessor(new PrivacyTraceProcessor())
        .AddProcessor(new SimpleActivityExportProcessor(traces)).Build();

    private static WebApplicationFactory<Program> CreateApp(Dictionary<string, string?> overrides, Action<IServiceCollection>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                    (service.ImplementationType == typeof(AttachmentReconciliationService) || service.ImplementationType == typeof(ProfileAvatarReconciliationService))).ToArray())
                    services.Remove(service);
                configure?.Invoke(services);
            });
        });

    private sealed class CapturedLogs : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed class CapturedTraces : BaseExporter<Activity>
    {
        public ConcurrentQueue<Span> Spans { get; } = new();
        public TaskCompletionSource ServerEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                Spans.Enqueue(new(activity.DisplayName, activity.Kind, activity.Status, activity.StatusDescription,
                    activity.TraceId, activity.SpanId, activity.TagObjects.ToDictionary(),
                    activity.Events.Select(entry => new Event(entry.Name, entry.Tags.ToDictionary())).ToArray()));
                if (activity.Kind == ActivityKind.Server) ServerEnded.TrySetResult();
            }
            return ExportResult.Success;
        }
    }

    private sealed record Event(string Name, Dictionary<string, object?> Tags)
    {
        public override string ToString() => $"{Name} {string.Join(' ', Tags)}";
    }
    private sealed record Span(string Name, ActivityKind Kind, ActivityStatusCode Status, string? StatusDescription,
        ActivityTraceId TraceId, ActivitySpanId SpanId, Dictionary<string, object?> Tags, Event[] Events)
    {
        public override string ToString() => $"{Name} {StatusDescription} {string.Join(' ', Tags)} {string.Join(' ', Events.Select(entry => entry.ToString()))}";
    }
}

// ActivitySource/Meter listeners are process-wide; these listener assertions run without competing hosts.
[CollectionDefinition("Observability", DisableParallelization = true)]
public sealed class ObservabilityCollection;
