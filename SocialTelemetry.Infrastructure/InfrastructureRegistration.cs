using System.Reflection;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Storage;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Identity;
using SocialTelemetry.Infrastructure.Observability;
using SocialTelemetry.Infrastructure.Persistence;
using SocialTelemetry.Infrastructure.Profiles;
using SocialTelemetry.Infrastructure.Runtime;
using SocialTelemetry.Infrastructure.Storage;
using SocialTelemetry.Infrastructure.Transcription;

namespace SocialTelemetry.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddSocialTelemetryInfrastructure(this IServiceCollection services,
        IConfiguration configuration, IWebHostEnvironment environment, Assembly hostAssembly)
    {
        services.AddSocialTelemetryDiagnostics(configuration, environment, hostAssembly);
        services.AddPersistence(configuration);
        services.AddIdentityFoundation();
        services.AddScoped<LocalProfileOperation>();
        services.AddScoped<LocalProfileSelectionService>();
        services.AddSingleton<LocalProfileLifetimes>();
        services.AddScoped<ProfileAiAccess>();
        services.AddSingleton<ProfileAiLifetimes>();

        services.AddOptions<ApplicationDataOptions>()
            .BindConfiguration("ApplicationData")
            .Validate(options => options.RootDirectory is null ||
                (Path.IsPathFullyQualified(options.RootDirectory) &&
                 ApplicationPaths.IsValidDirectory(options.RootDirectory, environment.ContentRootPath)),
                "ApplicationData requires a valid absolute root directory when configured.")
            .ValidateOnStart();
        services.AddSingleton<ApplicationPaths>();
        services.AddOptions<ChatGptOptions>()
            .BindConfiguration("ChatGpt")
            .ValidateDataAnnotations()
            .Validate(options => options.HasConsistentTiming(), "ChatGpt lock retry delay cannot exceed its timeout.")
            .Validate(options => options.IsValid(), "ChatGpt requires an HTTP 127.0.0.1 callback with the fixed callback path and an absolute local data directory.")
            .ValidateOnStart();
        services.AddOptions<UploadOptions>()
            .BindConfiguration("Uploads").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<TranscriptOptions>()
            .BindConfiguration("Transcripts").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<WhisperCppOptions>().BindConfiguration("WhisperCpp").ValidateDataAnnotations()
            .Validate(options => options.HasConsistentLimits(), "WhisperCpp decoded size must accommodate its duration limit.").ValidateOnStart();
        services.AddSingleton(NativeTranscriptionHost.FromHostAssembly(hostAssembly));
        services.AddSingleton<ITranscriptionProcessRunner, TranscriptionProcessRunner>();
        services.AddSingleton<ISpeechToTextClient, WhisperCppSpeechToTextClient>();
        services.AddOptions<AnalysisOptions>()
            .BindConfiguration("Analysis").ValidateDataAnnotations()
            .Validate(options => options.HasConsistentLimits(), "Analysis input limits must fit context, image count must fit evidence count, and total image bytes must cover an individual image.")
            .ValidateOnStart();
        services.AddOptions<StorageMaintenanceOptions>()
            .BindConfiguration("StorageMaintenance").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AttachmentStorageOptions>()
            .BindConfiguration("AttachmentStorage")
            .Validate<ApplicationPaths>((options, paths) => ApplicationPaths.IsValidDirectory(options.LocalDirectory, paths.RootDirectory),
                "AttachmentStorage requires a valid directory without symbolic links.")
            .ValidateOnStart();
        services.AddOptions<ProfileStorageOptions>()
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
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ChatGptCredentialStore>();
        services.AddSingleton<ChatGptConnection>();
        services.AddSingleton<ISystemBrowser, SystemBrowser>();
        services.AddHttpClient<ChatGptHttpClient>((services, client) =>
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
        services.AddScoped<IAiClient, ChatGptPlanAiClient>();
        services.AddKeyedSingleton<IFileStorage>("attachments", (services, _) =>
        {
            var paths = services.GetRequiredService<ApplicationPaths>();
            var options = services.GetRequiredService<IOptions<AttachmentStorageOptions>>().Value;
            return new LocalFileStorage(
                paths.Attachments(options),
                services.GetRequiredService<ILogger<LocalFileStorage>>());
        });
        services.AddScoped<IAttachmentStorage, LocalAttachmentStorage>();
        services.AddKeyedSingleton<IFileStorage>("avatars", (services, _) =>
        {
            var paths = services.GetRequiredService<ApplicationPaths>();
            var attachments = services.GetRequiredService<IOptions<AttachmentStorageOptions>>().Value;
            var profiles = services.GetRequiredService<IOptions<ProfileStorageOptions>>().Value;
            return new LocalFileStorage(paths.Avatars(profiles, attachments),
                services.GetRequiredService<ILogger<LocalFileStorage>>());
        });
        services.AddScoped<ProfileAvatarCleanup>();

        services.AddOptions<MediaBindingOptions>().BindConfiguration("MediaBinding").ValidateDataAnnotations()
            .Validate(options => options.LockRetryDelay <= options.LockTimeout, "MediaBinding retry delay cannot exceed its lock timeout.").ValidateOnStart();
        services.AddHostedService<MediaDirectoryBinding>();
        services.AddHostedService<ProfileAvatarReconciliationService>();
        services.AddHostedService<AttachmentReconciliationService>();
        return services;
    }
}
