using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.AI;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Tests.Infrastructure.Configuration;

public sealed class RuntimeConfigurationTests
{
    [Fact]
    public void Shipped_settings_bind_every_policy_and_preserve_previous_defaults()
    {
        using var application = CreateApp();
        using var client = application.CreateClient();
        var configuration = application.Services.GetRequiredService<IConfiguration>();
        CheckDefaults(application, configuration, "Uploads", new UploadOptions());
        CheckDefaults(application, configuration, "Analysis", new AnalysisOptions());
        CheckDefaults(application, configuration, "StorageMaintenance", new StorageMaintenanceOptions());
        CheckDefaults(application, configuration, "ChatGpt", new ChatGptOptions(), nameof(ChatGptOptions.DataDirectory));
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
    [InlineData("AttachmentStorage:LocalDirectory", " ")]
    [InlineData("ProfileStorage:AvatarDirectory", " ")]
    public void Invalid_policy_fails_at_startup(string key, string value)
    {
        using var application = CreateApp(new() { [key] = value });
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
        string section, T expected, string? excludedProperty = null) where T : class
    {
        var actual = application.Services.GetRequiredService<IOptions<T>>().Value;
        foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite && property.Name != excludedProperty))
        {
            Assert.NotNull(configuration[$"{section}:{property.Name}"]);
            Assert.Equal(property.GetValue(expected), property.GetValue(actual));
        }
    }

    private static WebApplicationFactory<Program> CreateApp(Dictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides ?? []));
            builder.ConfigureServices(services =>
            {
                // Configuration validation must not depend on a database or contact a provider.
                foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                    (service.ImplementationType == typeof(AttachmentReconciliationService) ||
                     service.ImplementationType == typeof(ProfileAvatarReconciliationService))).ToArray())
                    services.Remove(service);
            });
        });
}
