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
        services.AddScoped<SocialTelemetry.Application.Features.People.IPeoplePersistence, PeoplePersistence>();
        services.AddScoped<SocialTelemetry.Application.Features.PersonFacts.IPersonFactsPersistence, PersonFactsPersistence>();
        services.AddScoped<SocialTelemetry.Application.Features.UserProfile.IUserProfilePersistence, UserProfilePersistence>();
        services.AddScoped<SocialTelemetry.Application.Features.ProfileConnections.IProfileConnectionsPersistence, ProfileConnectionsPersistence>();
        services.AddScoped<SocialTelemetry.Application.Features.ProfileAvatars.IProfileAvatarPersistence, ProfileAvatarPersistence>();
        services.AddScoped(services => new SocialTelemetry.Application.Features.ProfileAvatars.ProfileAvatarService(
            services.GetRequiredService<SocialTelemetry.Application.Features.ProfileAvatars.IProfileAvatarPersistence>(),
            services.GetRequiredService<LocalProfileOperation>(),
            services.GetRequiredService<IOptions<UploadOptions>>().Value));
        services.AddScoped<SocialTelemetry.Application.Features.People.AddExternalConnection.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.Archive.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.Create.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.Delete.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.DeleteExternalConnection.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.GetAll.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.GetById.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.GetExternalConnection.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.GetExternalConnections.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.Unarchive.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.Update.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.People.UpdateExternalConnection.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.PersonFacts.Add.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.PersonFacts.Delete.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.PersonFacts.GetForPerson.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.PersonFacts.Update.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.UserProfile.AddExternalConnection.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.UserProfile.DeleteExternalConnection.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.UserProfile.Get.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.UserProfile.GetExternalConnection.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.UserProfile.GetExternalConnections.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.UserProfile.Update.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.UserProfile.UpdateExternalConnection.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.IInteractionPersistence, InteractionPersistence>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.IAttachmentPersistence, AttachmentPersistence>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.ITranscriptPersistence, TranscriptPersistence>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.Create.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.Delete.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.DeleteAttachment.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.DownloadAttachment.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.GetAttachment.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.GetById.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.GetForPerson.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.GetTranscript.Scenario>();
        services.AddScoped<SocialTelemetry.Application.Features.Interactions.Update.Scenario>();
        services.AddScoped(services => new SocialTelemetry.Application.Features.Interactions.AddAttachment.Scenario(
            services.GetRequiredService<SocialTelemetry.Application.Features.Interactions.IAttachmentPersistence>(),
            services.GetRequiredService<LocalProfileOperation>(), services.GetRequiredService<IAttachmentStorage>(),
            services.GetRequiredService<IOptions<UploadOptions>>().Value));
        services.AddScoped(services => new SocialTelemetry.Application.Features.Interactions.PutTranscript.Scenario(
            services.GetRequiredService<SocialTelemetry.Application.Features.Interactions.ITranscriptPersistence>(),
            services.GetRequiredService<LocalProfileOperation>(), services.GetRequiredService<IAttachmentStorage>(),
            services.GetRequiredService<IOptions<TranscriptOptions>>().Value, services.GetRequiredService<IOptions<UploadOptions>>().Value));
        services.AddScoped(services => new SocialTelemetry.Application.Features.Interactions.TranscribeAttachment.Scenario(
            services.GetRequiredService<SocialTelemetry.Application.Features.Interactions.ITranscriptPersistence>(),
            services.GetRequiredService<LocalProfileOperation>(), services.GetRequiredService<IAttachmentStorage>(),
            services.GetRequiredService<ISpeechToTextClient>(), services.GetRequiredService<IOptions<UploadOptions>>().Value,
            services.GetRequiredService<IOptions<TranscriptOptions>>().Value));

        services.AddOptions<MediaBindingOptions>().BindConfiguration("MediaBinding").ValidateDataAnnotations()
            .Validate(options => options.LockRetryDelay <= options.LockTimeout, "MediaBinding retry delay cannot exceed its lock timeout.").ValidateOnStart();
        services.AddHostedService<MediaDirectoryBinding>();
        services.AddHostedService<ProfileAvatarReconciliationService>();
        services.AddHostedService<AttachmentReconciliationService>();
        return services;
    }
}
