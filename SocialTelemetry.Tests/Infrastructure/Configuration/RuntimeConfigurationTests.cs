using SocialTelemetry.Application.Options;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SocialTelemetry.Infrastructure.AI;
using SocialTelemetry.Infrastructure.Storage;
using SocialTelemetry.Infrastructure.Runtime;

namespace SocialTelemetry.Tests.Infrastructure.Configuration;

public sealed class RuntimeConfigurationTests
{
    [Fact]
    public void Default_paths_preserve_existing_media_and_credential_locations()
    {
        using var application = CreateApp();
        using var client = application.CreateClient();
        var paths = application.Services.GetRequiredService<ApplicationPaths>();
        var environment = application.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.Equal(environment.ContentRootPath, paths.RootDirectory);
        Assert.Equal(Path.Combine(environment.ContentRootPath, "attachments"), paths.Attachments(new()));
        Assert.Equal(Path.Combine(environment.ContentRootPath, "attachments", "avatars"), paths.Avatars(new(), new()));
        var credentials = application.Services.GetRequiredService<IOptions<ChatGptOptions>>().Value.DataDirectory;
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SocialTelemetry", "ChatGpt"), paths.ResolveCredentialDirectory(credentials));
    }

    [Fact]
    public void Host_data_root_redirects_relative_media_without_moving_credentials_or_absolute_overrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
        using var application = CreateApp(new() { ["ApplicationData:RootDirectory"] = root });
        using var client = application.CreateClient();
        var paths = application.Services.GetRequiredService<ApplicationPaths>();
        Assert.Equal(Path.Combine(root, "attachments"), paths.Attachments(new()));
        Assert.Equal(Path.Combine(root, "attachments", "avatars"), paths.Avatars(new(), new()));
        var external = Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
        Assert.Equal(external, paths.Attachments(new() { LocalDirectory = external }));
        Assert.Equal(external, paths.Avatars(new() { AvatarDirectory = external }, new()));
        var credentials = application.Services.GetRequiredService<IOptions<ChatGptOptions>>().Value.DataDirectory;
        Assert.Equal(Path.GetFullPath(credentials), paths.ResolveCredentialDirectory(credentials));
    }

    [Theory]
    [InlineData("relative-directory")]
    [InlineData(" ")]
    public void Invalid_host_data_root_is_rejected_at_startup(string root)
    {
        using var application = CreateApp(new() { ["ApplicationData:RootDirectory"] = root });
        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(nameof(OptionsValidationException), exception.ToString());
    }

    [Fact]
    public void Application_version_is_available_from_assembly_metadata()
    {
        var assembly = typeof(Program).Assembly;
        Assert.NotNull(assembly.GetName().Version);
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Assert.NotNull(version);
        Assert.False(string.IsNullOrWhiteSpace(version.InformationalVersion));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Media_final_and_staging_directories_cannot_overlap(bool reversed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
        using var application = CreateApp(new()
        {
            ["AttachmentStorage:LocalDirectory"] = reversed ? Path.Combine(directory, ".staging") : directory,
            ["ProfileStorage:AvatarDirectory"] = reversed ? directory : Path.Combine(directory, ".staging")
        });
        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(nameof(OptionsValidationException), exception.ToString());
    }

    [Fact]
    public void Shipped_settings_bind_every_policy_and_preserve_previous_defaults()
    {
        using var application = CreateApp();
        using var client = application.CreateClient();
        var configuration = application.Services.GetRequiredService<IConfiguration>();
        CheckDefaults(application, configuration, "Uploads", new UploadOptions());
        CheckDefaults(application, configuration, "Analysis", new AnalysisOptions());
        CheckDefaults(application, configuration, "StorageMaintenance", new StorageMaintenanceOptions());
        CheckDefaults(application, configuration, "ChatGpt", new ChatGptOptions(), nameof(ChatGptOptions.DataDirectory), nameof(ChatGptOptions.CompatibilityProfiles));
        var profiles = application.Services.GetRequiredService<IOptions<ChatGptOptions>>().Value.CompatibilityProfiles;
        Assert.Equal(new[] { "gpt-5.6-sol", "gpt-5.6" }, profiles.Select(profile => profile.ModelId));
        Assert.All(profiles, profile =>
        {
            Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Supported, profile.Text);
            Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Supported, profile.Vision);
            Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Supported, profile.StructuredOutput);
        });
        var uploads = application.Services.GetRequiredService<IOptions<UploadOptions>>().Value;
        Assert.Equal(5 * 1024 * 1024, uploads.AvatarMaxBytes);
        Assert.Equal(10 * 1024 * 1024, uploads.AttachmentMaxBytes);
        Assert.Equal(6 * 1024 * 1024, uploads.AvatarRequestBodyBytes);
        Assert.Equal(11 * 1024 * 1024, uploads.AttachmentRequestBodyBytes);
    }

    [Fact]
    public void Configuration_overrides_bind_through_the_application_options_boundary()
    {
        using var application = CreateApp(new()
        {
            ["Uploads:AvatarMaxBytes"] = "4096",
            ["Analysis:PreviousInteractions"] = "2",
            ["StorageMaintenance:OrphanSafetyAge"] = "02:00:00",
            ["ChatGpt:HttpTimeout"] = "00:00:12",
            ["ChatGpt:RefreshTimeout"] = "00:00:09",
            ["ChatGpt:CredentialLockTimeout"] = "00:00:03",
            ["ChatGpt:CredentialLockRetryDelay"] = "00:00:00.025",
            ["ChatGpt:RevocationAttempts"] = "3"
        });
        using var client = application.CreateClient();
        Assert.Equal(4096, application.Services.GetRequiredService<IOptions<UploadOptions>>().Value.AvatarMaxBytes);
        Assert.Equal(2, application.Services.GetRequiredService<IOptions<AnalysisOptions>>().Value.PreviousInteractions);
        Assert.Equal(TimeSpan.FromHours(2), application.Services.GetRequiredService<IOptions<StorageMaintenanceOptions>>().Value.OrphanSafetyAge);
        var provider = application.Services.GetRequiredService<IOptions<ChatGptOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(12), provider.HttpTimeout);
        Assert.Equal(TimeSpan.FromSeconds(9), provider.RefreshTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), provider.CredentialLockTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(25), provider.CredentialLockRetryDelay);
        Assert.Equal(3, provider.RevocationAttempts);
    }

    [Theory]
    [InlineData("Uploads:AvatarMaxBytes", "0")]
    [InlineData("Uploads:AttachmentMaxBytes", "-1")]
    [InlineData("Uploads:AvatarMaxBytes", "2147483647")]
    [InlineData("Uploads:AttachmentMaxBytes", "2147483647")]
    [InlineData("Uploads:MultipartOverheadBytes", "0")]
    [InlineData("Uploads:MultipartOverheadBytes", "2147483647")]
    [InlineData("Analysis:Participants", "0")]
    [InlineData("Analysis:FactsPerPerson", "-1")]
    [InlineData("Analysis:PreviousInteractions", "2147483647")]
    [InlineData("Analysis:ImageBytes", "2147483647")]
    [InlineData("Analysis:TotalImageBytes", "1")]
    [InlineData("Analysis:EvidenceCount", "1")]
    [InlineData("Analysis:ContextCharacters", "100")]
    [InlineData("Analysis:ContextCharacters", "2147483647")]
    [InlineData("Analysis:ResultCharacters", "2147483647")]
    [InlineData("StorageMaintenance:OrphanSafetyAge", "00:00:00")]
    [InlineData("StorageMaintenance:OrphanSafetyAge", "31.00:00:00")]
    [InlineData("ChatGpt:HttpTimeout", "00:00:00")]
    [InlineData("ChatGpt:InferenceTimeout", "00:11:00")]
    [InlineData("ChatGpt:HttpResponseBufferBytes", "2147483647")]
    [InlineData("ChatGpt:StreamResponseCharacters", "2147483647")]
    [InlineData("ChatGpt:CredentialLockTimeout", "00:00:00.001")]
    [InlineData("ChatGpt:RevocationAttempts", "0")]
    [InlineData("ChatGpt:RevocationAttempts", "100")]
    [InlineData("ChatGpt:CompatibilityProfiles:0:ModelId", " ")]
    [InlineData("ChatGpt:CompatibilityProfiles:0:Vision", "999")]
    [InlineData("AttachmentStorage:LocalDirectory", " ")]
    [InlineData("ProfileStorage:AvatarDirectory", " ")]
    public void Invalid_policy_fails_at_startup(string key, string value)
    {
        using var application = CreateApp(new() { [key] = value });
        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(nameof(OptionsValidationException), exception.ToString());
    }

    [Theory]
    [InlineData("provider-new")]
    [InlineData("GPT-5.6-SOL")]
    public void Shipped_JSON_and_partial_overlay_never_transfer_assertions_between_exact_model_IDs(string modelId)
    {
        using var application = CreateApp(new()
        {
            ["ChatGpt:CompatibilityProfiles:0:ModelId"] = modelId,
            ["ChatGpt:CompatibilityProfiles:0:Text"] = "Supported"
        });
        using var client = application.CreateClient();
        var resolver = application.Services.GetRequiredService<ChatGptCapabilityResolver>();
        var capabilities = resolver.Resolve(modelId);
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Supported, capabilities.Text);
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unknown, capabilities.Vision);
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unknown, capabilities.StructuredOutput);
        Assert.True(resolver.Resolve("gpt-5.6-sol").Supports(SocialTelemetry.Application.Contracts.AI.Models.AiCapability.Vision));
        Assert.True(resolver.Resolve("gpt-5.6").Supports(SocialTelemetry.Application.Contracts.AI.Models.AiCapability.StructuredOutput));
    }

    [Fact]
    public void Existing_exact_ID_overrides_only_its_explicit_states_and_preserves_other_presets()
    {
        using var application = CreateApp(new()
        {
            ["ChatGpt:CompatibilityProfiles:0:ModelId"] = "gpt-5.6-sol",
            ["ChatGpt:CompatibilityProfiles:0:Vision"] = "Unsupported"
        });
        using var client = application.CreateClient();
        var resolver = application.Services.GetRequiredService<ChatGptCapabilityResolver>();
        var capabilities = resolver.Resolve("gpt-5.6-sol");
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Supported, capabilities.Text);
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unsupported, capabilities.Vision);
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Supported, capabilities.StructuredOutput);
        Assert.True(resolver.Resolve("gpt-5.6").Supports(SocialTelemetry.Application.Contracts.AI.Models.AiCapability.Vision));
        Assert.Empty(resolver.Resolve("GPT-5.6-SOL").SupportedCapabilities);
    }

    [Fact]
    public void Chained_shipped_JSON_and_layered_overrides_keep_original_identity_boundaries()
    {
        using var layered = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatGpt:CompatibilityProfiles:0:ModelId"] = "provider-new",
                ["ChatGpt:CompatibilityProfiles:0:Text"] = "Supported"
            }).Build();
        using var application = CreateApp(chained: layered);
        using var client = application.CreateClient();
        var resolver = application.Services.GetRequiredService<ChatGptCapabilityResolver>();
        var capabilities = resolver.Resolve("provider-new");
        Assert.True(capabilities.Supports(SocialTelemetry.Application.Contracts.AI.Models.AiCapability.Text));
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unknown, capabilities.Vision);
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unknown, capabilities.StructuredOutput);
        Assert.True(resolver.Resolve("gpt-5.6-sol").Supports(SocialTelemetry.Application.Contracts.AI.Models.AiCapability.Vision));
    }

    [Fact]
    public void Environment_provider_partial_new_ID_cannot_inherit_shipped_assertions()
    {
        var prefix = "SocialTelemetryCapabilityTest_" + Guid.NewGuid().ToString("N") + "_";
        var identity = prefix + "ChatGpt__CompatibilityProfiles__0__ModelId";
        var text = prefix + "ChatGpt__CompatibilityProfiles__0__Text";
        try
        {
            Environment.SetEnvironmentVariable(identity, "env-provider-model");
            Environment.SetEnvironmentVariable(text, "Supported");
            using var environment = (ConfigurationRoot)new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            using var application = CreateApp(chained: environment);
            using var client = application.CreateClient();
            var capabilities = application.Services.GetRequiredService<ChatGptCapabilityResolver>().Resolve("env-provider-model");
            Assert.True(capabilities.Supports(SocialTelemetry.Application.Contracts.AI.Models.AiCapability.Text));
            Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unknown, capabilities.Vision);
            Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unknown, capabilities.StructuredOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(identity, null);
            Environment.SetEnvironmentVariable(text, null);
        }
    }

    [Fact]
    public void Case_distinct_profile_values_and_explicit_Unknown_are_preserved_by_actual_binding()
    {
        using var application = CreateApp(new()
        {
            ["ChatGpt:CompatibilityProfiles:0:ModelId"] = "model",
            ["ChatGpt:CompatibilityProfiles:0:Text"] = "Supported",
            ["ChatGpt:CompatibilityProfiles:1:ModelId"] = "MODEL",
            ["ChatGpt:CompatibilityProfiles:1:Text"] = "Unsupported",
            ["ChatGpt:CompatibilityProfiles:2:ModelId"] = "gpt-5.6-sol",
            ["ChatGpt:CompatibilityProfiles:2:Vision"] = "Unknown"
        });
        using var client = application.CreateClient();
        var resolver = application.Services.GetRequiredService<ChatGptCapabilityResolver>();
        Assert.True(resolver.Resolve("model").Supports(SocialTelemetry.Application.Contracts.AI.Models.AiCapability.Text));
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unsupported, resolver.Resolve("MODEL").Text);
        Assert.Empty(resolver.Resolve("Model").SupportedCapabilities);
        Assert.Equal(SocialTelemetry.Application.Contracts.AI.Models.AiCapabilitySupport.Unknown, resolver.Resolve("gpt-5.6-sol").Vision);
        Assert.True(resolver.Resolve("gpt-5.6-sol").Supports(SocialTelemetry.Application.Contracts.AI.Models.AiCapability.Text));
    }

    [Theory]
    [InlineData("Vision", "Suppported")]
    [InlineData("Vision", "999")]
    [InlineData("Visoin", "sensitive-config-value")]
    [InlineData("Vision:Nested", "sensitive-config-value")]
    [InlineData("ModelId", " ")]
    [InlineData("", "sensitive-config-value")]
    public void Malformed_layered_profiles_fail_at_startup_without_dropping_entries_or_exposing_values(string field, string value)
    {
        var path = "ChatGpt:CompatibilityProfiles:0";
        using var application = CreateApp(new()
        {
            [$"{path}:ModelId"] = "private-model-id",
            [field.Length == 0 ? path : $"{path}:{field}"] = value
        });
        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(nameof(OptionsValidationException), exception.ToString());
        Assert.DoesNotContain("sensitive-config-value", exception.ToString());
        Assert.DoesNotContain("Suppported", exception.ToString());
        Assert.DoesNotContain("private-model-id", exception.ToString());
    }

    [Fact]
    public void Duplicate_exact_IDs_in_one_overlay_fail_instead_of_last_entry_winning()
    {
        using var application = CreateApp(new()
        {
            ["ChatGpt:CompatibilityProfiles:0:ModelId"] = "duplicate-model",
            ["ChatGpt:CompatibilityProfiles:0:Text"] = "Supported",
            ["ChatGpt:CompatibilityProfiles:1:ModelId"] = "duplicate-model",
            ["ChatGpt:CompatibilityProfiles:1:Text"] = "Unsupported"
        });
        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(nameof(OptionsValidationException), exception.ToString());
    }

    [Fact]
    public void State_only_overlay_requires_explicit_ID_instead_of_inheriting_identity_by_position()
    {
        using var application = CreateApp(new() { ["ChatGpt:CompatibilityProfiles:0:Vision"] = "Supported" });
        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(nameof(OptionsValidationException), exception.ToString());
    }

    [Fact]
    public void Avatar_and_attachment_areas_cannot_share_the_same_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SocialTelemetry.Tests", Guid.NewGuid().ToString("N"));
        using var application = CreateApp(new()
        {
            ["AttachmentStorage:LocalDirectory"] = directory,
            ["ProfileStorage:AvatarDirectory"] = directory
        });
        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());
        Assert.Contains(nameof(OptionsValidationException), exception.ToString());
    }

    private static void CheckDefaults<T>(WebApplicationFactory<Program> application, IConfiguration configuration,
        string section, T expected, params string[] excludedProperties) where T : class
    {
        var actual = application.Services.GetRequiredService<IOptions<T>>().Value;
        foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite && !excludedProperties.Contains(property.Name)))
        {
            Assert.NotNull(configuration[$"{section}:{property.Name}"]);
            Assert.Equal(property.GetValue(expected), property.GetValue(actual));
        }
    }

    private static WebApplicationFactory<Program> CreateApp(Dictionary<string, string?>? overrides = null, IConfiguration? chained = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(overrides ?? []);
                if (chained is not null) configuration.AddConfiguration(chained);
            });
            builder.ConfigureServices(services =>
            {
                // Configuration validation must not depend on a database or contact a provider.
                foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                    (service.ImplementationType == typeof(AttachmentReconciliationService) ||
                     service.ImplementationType == typeof(ProfileAvatarReconciliationService) ||
                     service.ImplementationType == typeof(MediaDirectoryBinding))).ToArray())
                    services.Remove(service);
            });
        });
}
