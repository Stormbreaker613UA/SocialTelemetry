using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using SocialTelemetry.Api.Common.Exceptions;
using SocialTelemetry.Api.Features.AiConnection;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.Persistence;
using SocialTelemetry.Api.Infrastructure.Storage;
using SocialTelemetry.Api.Infrastructure.Runtime;
using SocialTelemetry.Api.Infrastructure.Observability;
using SocialTelemetry.Api.Infrastructure.Transcription;

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

builder.Services.AddSocialTelemetryDiagnostics(builder.Configuration, builder.Environment);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();
builder.Services.AddOptions<ApplicationDataOptions>()
    .BindConfiguration("ApplicationData")
    .Validate(options => options.RootDirectory is null ||
        (Path.IsPathFullyQualified(options.RootDirectory) &&
         ApplicationPaths.IsValidDirectory(options.RootDirectory, builder.Environment.ContentRootPath)),
        "ApplicationData requires a valid absolute root directory when configured.")
    .ValidateOnStart();
builder.Services.AddSingleton<ApplicationPaths>();
builder.Services.AddOptions<ChatGptOptions>()
    .BindConfiguration("ChatGpt")
    .ValidateDataAnnotations()
    .Validate(options => options.HasConsistentTiming(), "ChatGpt lock retry delay cannot exceed its timeout.")
    .Validate(options => options.IsValid(), "ChatGpt requires an HTTP 127.0.0.1 callback with the fixed callback path and an absolute local data directory.")
    .ValidateOnStart();
builder.Services.AddOptions<UploadOptions>()
    .BindConfiguration("Uploads").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TranscriptOptions>()
    .BindConfiguration("Transcripts").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<WhisperCppOptions>().BindConfiguration("WhisperCpp").ValidateDataAnnotations()
    .Validate(options => options.HasConsistentLimits(), "WhisperCpp decoded size must accommodate its duration limit.").ValidateOnStart();
builder.Services.AddSingleton<ITranscriptionProcessRunner, TranscriptionProcessRunner>();
builder.Services.AddSingleton<ISpeechToTextClient, WhisperCppSpeechToTextClient>();
builder.Services.AddOptions<AnalysisOptions>()
    .BindConfiguration("Analysis").ValidateDataAnnotations()
    .Validate(options => options.HasConsistentLimits(), "Analysis input limits must fit context, image count must fit evidence count, and total image bytes must cover an individual image.")
    .ValidateOnStart();
builder.Services.AddOptions<StorageMaintenanceOptions>()
    .BindConfiguration("StorageMaintenance").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AttachmentStorageOptions>()
    .BindConfiguration("AttachmentStorage")
    .Validate<ApplicationPaths>((options, paths) => ApplicationPaths.IsValidDirectory(options.LocalDirectory, paths.RootDirectory),
        "AttachmentStorage requires a valid directory without symbolic links.")
    .ValidateOnStart();
builder.Services.AddOptions<ProfileStorageOptions>()
    .BindConfiguration("ProfileStorage")
    .Validate<IOptions<AttachmentStorageOptions>, ApplicationPaths>((options, attachments, paths) =>
    {
        var attachmentPath = paths.Attachments(attachments.Value);
        var directory = options.AvatarDirectory ?? Path.Combine(attachmentPath, "avatars");
        if (!ApplicationPaths.IsValidDirectory(directory, paths.RootDirectory)) return false;
        var avatarPath = paths.Avatars(options, attachments.Value);
        return ApplicationPaths.AreStorageAreasSeparate(attachmentPath, avatarPath);
    }, "ProfileStorage requires a valid avatar directory separate from attachment files and staging.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChatGptCredentialStore>();
builder.Services.AddSingleton<ChatGptConnection>();
builder.Services.AddSingleton<ISystemBrowser, SystemBrowser>();
builder.Services.AddHttpClient<ChatGptHttpClient>((services, client) =>
    {
        var options = services.GetRequiredService<IOptions<ChatGptOptions>>().Value;
        client.Timeout = options.HttpTimeout;
        client.MaxResponseContentBufferSize = options.HttpResponseBufferBytes;
    })
    .ConfigurePrimaryHttpMessageHandler(services => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = services.GetRequiredService<IOptions<ChatGptOptions>>().Value.PooledConnectionLifetime
    })
    .RemoveAllLoggers();
builder.Services.AddScoped<IAiClient, ChatGptPlanAiClient>();
builder.Services.AddKeyedSingleton<IFileStorage>("attachments", (services, _) =>
{
    var paths = services.GetRequiredService<ApplicationPaths>();
    var options = services.GetRequiredService<IOptions<AttachmentStorageOptions>>().Value;
    return new LocalFileStorage(
        paths.Attachments(options),
        services.GetRequiredService<ILogger<LocalFileStorage>>());
});
builder.Services.AddScoped<IAttachmentStorage, LocalAttachmentStorage>();
builder.Services.AddKeyedSingleton<IFileStorage>("avatars", (services, _) =>
{
    var paths = services.GetRequiredService<ApplicationPaths>();
    var attachments = services.GetRequiredService<IOptions<AttachmentStorageOptions>>().Value;
    var profiles = services.GetRequiredService<IOptions<ProfileStorageOptions>>().Value;
    return new LocalFileStorage(paths.Avatars(profiles, attachments),
        services.GetRequiredService<ILogger<LocalFileStorage>>());
});
builder.Services.AddScoped<ProfileAvatarCleanup>();
builder.Services.AddScoped<ProfileAvatarService>();
builder.Services.AddOptions<MediaBindingOptions>().BindConfiguration("MediaBinding").ValidateDataAnnotations()
    .Validate(options => options.LockRetryDelay <= options.LockTimeout, "MediaBinding retry delay cannot exceed its lock timeout.").ValidateOnStart();
builder.Services.AddHostedService<MediaDirectoryBinding>();
builder.Services.AddHostedService<ProfileAvatarReconciliationService>();
builder.Services.AddHostedService<AttachmentReconciliationService>();
builder.Services.AddScoped<AiContextBuilder>();

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<MediaBindingOptions>>().Value.Action is not null)
{
    await app.Services.GetServices<IHostedService>().OfType<MediaDirectoryBinding>().Single().RunMaintenanceAsync(CancellationToken.None);
    return;
}

app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
app.UseExceptionHandler();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/ai-connection") ||
    context.GetEndpoint()?.Metadata.GetMetadata<RequireLocalAiRequestAttribute>() is not null,
    localConnection => localConnection.UseMiddleware<LocalAiConnectionMiddleware>());
// OpenAI requires an HTTP 127.0.0.1 callback. Other API routes keep their HTTPS behavior.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/ai-connection"), api => api.UseHttpsRedirection());
app.UseFastEndpoints();
app.UseSwaggerGen();

app.Run();

public partial class Program;
