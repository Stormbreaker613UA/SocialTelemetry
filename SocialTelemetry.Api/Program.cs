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
        .WriteTo.Console();
}, preserveStaticLogger: true);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is required.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();
builder.Services.AddOptions<ChatGptOptions>()
    .BindConfiguration("ChatGpt")
    .ValidateDataAnnotations()
    .Validate(options => options.HasConsistentTiming(), "ChatGpt lock retry delay cannot exceed its timeout.")
    .Validate(options => options.IsValid(), "ChatGpt requires an HTTP 127.0.0.1 callback with the fixed callback path and an absolute local data directory.")
    .ValidateOnStart();
builder.Services.AddOptions<UploadOptions>()
    .BindConfiguration("Uploads").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AnalysisOptions>()
    .BindConfiguration("Analysis").ValidateDataAnnotations()
    .Validate(options => options.HasConsistentLimits(), "Analysis input limits must fit context, image count must fit evidence count, and total image bytes must cover an individual image.")
    .ValidateOnStart();
builder.Services.AddOptions<StorageMaintenanceOptions>()
    .BindConfiguration("StorageMaintenance").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AttachmentStorageOptions>()
    .BindConfiguration("AttachmentStorage")
    .Validate(options => StorageDirectories.IsValid(options.LocalDirectory, builder.Environment.ContentRootPath),
        "AttachmentStorage requires a valid directory without symbolic links.")
    .ValidateOnStart();
builder.Services.AddOptions<ProfileStorageOptions>()
    .BindConfiguration("ProfileStorage")
    .Validate<IOptions<AttachmentStorageOptions>>((options, attachments) =>
    {
        var attachmentPath = StorageDirectories.Attachments(attachments.Value, builder.Environment.ContentRootPath);
        var directory = options.AvatarDirectory ?? Path.Combine(attachmentPath, "avatars");
        if (!StorageDirectories.IsValid(directory, builder.Environment.ContentRootPath)) return false;
        var avatarPath = StorageDirectories.Avatars(options, attachments.Value, builder.Environment.ContentRootPath);
        return !string.Equals(Path.TrimEndingDirectorySeparator(attachmentPath), Path.TrimEndingDirectorySeparator(avatarPath),
            StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.Combine(attachmentPath, ".staging"), Path.TrimEndingDirectorySeparator(avatarPath), StringComparison.OrdinalIgnoreCase);
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
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    var options = services.GetRequiredService<IOptions<AttachmentStorageOptions>>().Value;
    return new LocalFileStorage(
        StorageDirectories.Attachments(options, environment.ContentRootPath),
        services.GetRequiredService<ILogger<LocalFileStorage>>());
});
builder.Services.AddScoped<IAttachmentStorage, LocalAttachmentStorage>();
builder.Services.AddKeyedSingleton<IFileStorage>("avatars", (services, _) =>
{
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    var attachments = services.GetRequiredService<IOptions<AttachmentStorageOptions>>().Value;
    var profiles = services.GetRequiredService<IOptions<ProfileStorageOptions>>().Value;
    return new LocalFileStorage(StorageDirectories.Avatars(profiles, attachments, environment.ContentRootPath),
        services.GetRequiredService<ILogger<LocalFileStorage>>());
});
builder.Services.AddScoped<ProfileAvatarCleanup>();
builder.Services.AddScoped<ProfileAvatarService>();
builder.Services.AddHostedService<ProfileAvatarReconciliationService>();
builder.Services.AddHostedService<AttachmentReconciliationService>();
builder.Services.AddScoped<AiContextBuilder>();

var app = builder.Build();

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
