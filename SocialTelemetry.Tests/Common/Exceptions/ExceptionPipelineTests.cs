using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Serilog.Core;
using Serilog.Events;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Tests.Common.Exceptions;

public sealed class ExceptionPipelineTests
{
    [Theory]
    [InlineData("not-found", "application/problem+json", HttpStatusCode.NotFound)]
    [InlineData("conflict", "application/problem+json", HttpStatusCode.Conflict)]
    [InlineData("unknown", "application/problem+json", HttpStatusCode.InternalServerError)]
    [InlineData("unknown", "image/png", HttpStatusCode.InternalServerError)]
    [InlineData("bad-request", "application/problem+json", HttpStatusCode.BadRequest)]
    [InlineData("too-large", "application/problem+json", HttpStatusCode.RequestEntityTooLarge)]
    public async Task Pipeline_returns_safe_problem_details_and_logs_only_safe_metadata(
        string failure, string accept, HttpStatusCode expectedStatus)
    {
        var sink = new CapturedEvents();
        using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(accept == "image/png" ? "Production" : "Development");
            builder.UseSetting("ConnectionStrings:Default", "Host=localhost;Database=unused");
            builder.ConfigureServices(services =>
            {
                // This pipeline-only host deliberately has no database.
                var reconciliation = services.Single(service => service.ServiceType == typeof(IHostedService) &&
                    service.ImplementationType == typeof(AttachmentReconciliationService));
                services.Remove(reconciliation);
                services.AddSingleton<ILogEventSink>(sink);
                services.AddSingleton<IStartupFilter, FailureStartupFilter>();
            });
        });
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);

        using var response = await client.GetAsync($"/test/failure/{failure}?private={FailureStartupFilter.PrivateMarker}", TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal((int)expectedStatus, problem.Status);
        Assert.DoesNotContain(FailureStartupFilter.PrivateMarker, body);
        Assert.DoesNotContain("Exception", body);

        var loggedContent = string.Join('\n', sink.Events.Select(logEvent =>
            $"{logEvent.RenderMessage()} {logEvent.Exception} {string.Join(' ', logEvent.Properties)}"));
        Assert.DoesNotContain(FailureStartupFilter.PrivateMarker, loggedContent);
        Assert.Contains(sink.Events, logEvent => logEvent.Properties.ContainsKey("Elapsed"));
        if (failure == "unknown")
        {
            Assert.Contains(sink.Events, logEvent => logEvent.Properties.ContainsKey("ExceptionType"));
        }
    }

    private sealed class CapturedEvents : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed class FailureStartupFilter : IStartupFilter
    {
        public const string PrivateMarker = "PRIVATE_SOCIAL_CONTENT_AND_PROVIDER_DETAIL";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            next(application);
            application.Use(async (httpContext, nextMiddleware) =>
            {
                if (!httpContext.Request.Path.StartsWithSegments("/test/failure"))
                {
                    await nextMiddleware();
                    return;
                }

                var exception = new InvalidOperationException(PrivateMarker);
                var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Update")
                    .LogError(exception, "Provider failure {PrivateValue}", PrivateMarker);

                throw httpContext.Request.Path.Value?.Split('/').Last() switch
                {
                    "not-found" => new NotFoundException(PrivateMarker),
                    "conflict" => new ConflictException(PrivateMarker),
                    "bad-request" => new BadHttpRequestException(PrivateMarker, StatusCodes.Status400BadRequest),
                    "too-large" => new BadHttpRequestException(PrivateMarker, StatusCodes.Status413PayloadTooLarge),
                    _ => exception
                };
            });
        };
    }
}
