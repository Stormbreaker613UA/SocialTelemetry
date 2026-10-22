using SocialTelemetry.Infrastructure;
using SocialTelemetry.Api.Common.Profiles;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Features.AiConnection;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Storage;
using SocialTelemetry.Infrastructure.Transcription;
using SocialTelemetry.Infrastructure.Identity;

var launcherExit = NativeTranscriptionBootstrap.RunIfRequested(args);
if (launcherExit is { } exitCode)
{
    Environment.ExitCode = exitCode;
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        // Provider failure events can contain database details and private values.
        .Filter.ByExcluding(Matching.FromSource("Microsoft.EntityFrameworkCore"))
        .Filter.ByExcluding(Matching.FromSource("Npgsql"))
        .Filter.ByExcluding(Matching.FromSource("Microsoft.IdentityModel"))
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {TraceId} {SpanId}{NewLine}{Exception}");
}, preserveStaticLogger: true);

builder.Services.AddSocialTelemetryInfrastructure(builder.Configuration, builder.Environment, typeof(Program).Assembly);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddFastEndpoints();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-SocialTelemetry-CSRF";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddSingleton(new LocalAccessTransport(LocalAccessRegistration.Scheme, LocalAccessRegistration.DeviceClaim));
builder.Services.SwaggerDocument();
builder.Services.AddSingleton(services =>
{
    var callback = new Uri(services.GetRequiredService<IOptions<ChatGptOptions>>().Value.CallbackUri);
    if (callback.AbsolutePath != AiConnectionRoutes.CallbackPath)
        throw new InvalidOperationException("The configured provider callback must match the HTTP callback route.");
    return new LocalAiTransport(callback);
});

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<MediaBindingOptions>>().Value.Action is not null)
{
    await app.Services.GetServices<IHostedService>().OfType<MediaDirectoryBinding>().Single().RunMaintenanceAsync(CancellationToken.None);
    return;
}

app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
app.UseExceptionHandler();
app.UseRouting();
app.UseMiddleware<LocalAccessHttpMiddleware>();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/ai-connection") || context.Request.Path.StartsWithSegments("/local-profiles") ||
    context.GetEndpoint()?.Metadata.GetMetadata<RequireLocalAiRequestAttribute>() is not null,
    localConnection => localConnection.UseMiddleware<LocalAiConnectionMiddleware>());
app.UseMiddleware<LocalProfileMiddleware>();
// OpenAI requires an HTTP 127.0.0.1 callback. Other API routes keep their HTTPS behavior.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/ai-connection"), api => api.UseHttpsRedirection());
app.UseFastEndpoints();
app.UseSwaggerGen();

app.Run();

public partial class Program;
