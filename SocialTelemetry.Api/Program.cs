using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.EntityFrameworkCore;
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
    .Validate(options => options.IsValid(), "ChatGpt requires an HTTP 127.0.0.1 callback with the fixed callback path and an absolute local data directory.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChatGptCredentialStore>();
builder.Services.AddSingleton<ChatGptConnection>();
builder.Services.AddSingleton<ISystemBrowser, SystemBrowser>();
builder.Services.AddHttpClient<ChatGptHttpClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.MaxResponseContentBufferSize = 1024 * 1024;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    .RemoveAllLoggers();
builder.Services.AddScoped<IAiClient, ChatGptPlanAiClient>();
builder.Services.AddKeyedSingleton<IFileStorage>("attachments", (services, _) =>
{
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    var configuration = services.GetRequiredService<IConfiguration>();
    var directory = configuration["AttachmentStorage:LocalDirectory"];
    return new LocalFileStorage(
        Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? "attachments" : directory, environment.ContentRootPath),
        services.GetRequiredService<ILogger<LocalFileStorage>>());
});
builder.Services.AddScoped<IAttachmentStorage, LocalAttachmentStorage>();
builder.Services.AddKeyedSingleton<IFileStorage>("avatars", (services, _) =>
{
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    var configuration = services.GetRequiredService<IConfiguration>();
    var attachmentDirectory = configuration["AttachmentStorage:LocalDirectory"] ?? "attachments";
    var directory = configuration["ProfileStorage:AvatarDirectory"]
        ?? Path.Combine(attachmentDirectory, "avatars");
    return new LocalFileStorage(Path.GetFullPath(directory, environment.ContentRootPath),
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
