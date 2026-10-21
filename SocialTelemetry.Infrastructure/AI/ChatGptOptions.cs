using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Infrastructure.Runtime;

namespace SocialTelemetry.Infrastructure.AI;

public sealed class ChatGptOptions
{
    private const string CompatibilityPath = "ChatGpt:CompatibilityProfiles";
    public const string CallbackPath = "/ai-connection/chatgpt/callback";
    public string CallbackUri { get; set; } = "http://127.0.0.1:5059" + CallbackPath;
    public string DataDirectory { get; set; } = ApplicationPaths.DefaultCredentialDirectory("ChatGpt");
    public List<ChatGptModelCompatibilityProfile> CompatibilityProfiles { get; set; } = [];

    [Range(typeof(TimeSpan), "00:00:00.001", "00:05:00")]
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);
    [Range(1024, 8 * 1024 * 1024)]
    public int HttpResponseBufferBytes { get; set; } = 1024 * 1024;
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(5);
    [Range(typeof(TimeSpan), "00:00:00.001", "00:02:00")]
    public TimeSpan CredentialLockTimeout { get; set; } = TimeSpan.FromSeconds(15);
    [Range(typeof(TimeSpan), "00:00:00.001", "00:00:01")]
    public TimeSpan CredentialLockRetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);
    [Range(typeof(TimeSpan), "00:00:00.001", "00:10:00")]
    public TimeSpan InferenceTimeout { get; set; } = TimeSpan.FromMinutes(2);
    [Range(1024, 8 * 1024 * 1024)]
    public int StreamResponseCharacters { get; set; } = 2 * 1024 * 1024;
    [Range(typeof(TimeSpan), "00:00:00.001", "00:02:00")]
    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(30);
    [Range(typeof(TimeSpan), "00:00:01", "00:30:00")]
    public TimeSpan AuthorizationLifetime { get; set; } = TimeSpan.FromMinutes(10);
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan SigningKeyCacheLifetime { get; set; } = TimeSpan.FromMinutes(30);
    [Range(1, 5)] public int RevocationAttempts { get; set; } = 2;
    [Range(typeof(TimeSpan), "00:00:00.001", "00:00:05")]
    public TimeSpan RevocationRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public bool HasConsistentTiming() => CredentialLockRetryDelay <= CredentialLockTimeout;

    internal static void Bind(ChatGptOptions options, IConfiguration configuration)
    {
        try
        {
            configuration.GetSection("ChatGpt").Bind(options, binder => binder.ErrorOnUnknownConfiguration = true);
            options.CompatibilityProfiles = BindCompatibilityProfiles(configuration);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Conversion exceptions can include configuration values. Never publish partially
            // bound options or include those values in the startup validation error.
            throw InvalidCompatibilityConfiguration();
        }
    }

    private static List<ChatGptModelCompatibilityProfile> BindCompatibilityProfiles(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
            throw InvalidCompatibilityConfiguration();

        var profiles = new Dictionary<string, ChatGptModelCompatibilityProfile>(StringComparer.Ordinal);
        foreach (var provider in ConfigurationLayers(root))
        {
            if (provider.TryGet(CompatibilityPath, out var scalar) && !string.IsNullOrEmpty(scalar))
                throw InvalidCompatibilityConfiguration();
            var layerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var index in provider.GetChildKeys([], CompatibilityPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw InvalidCompatibilityConfiguration();
                var path = $"{CompatibilityPath}:{index}";
                ValidateProfileFields(provider, path);
                if (!provider.TryGet($"{path}:ModelId", out var modelId) || string.IsNullOrWhiteSpace(modelId) || !layerIds.Add(modelId))
                    throw InvalidCompatibilityConfiguration();

                // Array positions identify entries only inside ONE provider. Across layers,
                // inherit assertions solely from an explicit, ordinal-equal model identity.
                profiles.TryGetValue(modelId, out var previous);
                var profile = new ChatGptModelCompatibilityProfile
                {
                    ModelId = modelId,
                    Text = ReadSupport(provider, path, nameof(ChatGptModelCompatibilityProfile.Text), previous?.Text),
                    Vision = ReadSupport(provider, path, nameof(ChatGptModelCompatibilityProfile.Vision), previous?.Vision),
                    StructuredOutput = ReadSupport(provider, path, nameof(ChatGptModelCompatibilityProfile.StructuredOutput), previous?.StructuredOutput)
                };
                if (!profile.IsValid()) throw InvalidCompatibilityConfiguration();
                profiles[modelId] = profile;
            }
        }
        return profiles.Values.ToList();
    }

    private static IEnumerable<IConfigurationProvider> ConfigurationLayers(IConfigurationRoot root)
    {
        foreach (var provider in root.Providers)
        {
            if (provider is not ChainedConfigurationProvider chained)
                yield return provider;
            else if (chained.Configuration is IConfigurationRoot source)
            {
                foreach (var layer in ConfigurationLayers(source)) yield return layer;
            }
            else
                throw InvalidCompatibilityConfiguration();
        }
    }

    private static void ValidateProfileFields(IConfigurationProvider provider, string path)
    {
        if (provider.TryGet(path, out var scalar) && scalar is not null)
            throw InvalidCompatibilityConfiguration();
        string[] allowed = [nameof(ChatGptModelCompatibilityProfile.ModelId), nameof(ChatGptModelCompatibilityProfile.Text),
            nameof(ChatGptModelCompatibilityProfile.Vision), nameof(ChatGptModelCompatibilityProfile.StructuredOutput)];
        foreach (var field in provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!allowed.Contains(field, StringComparer.OrdinalIgnoreCase) || provider.GetChildKeys([], $"{path}:{field}").Any())
                throw InvalidCompatibilityConfiguration();
    }

    private static AiCapabilitySupport ReadSupport(IConfigurationProvider provider, string path, string field, AiCapabilitySupport? previous)
    {
        if (!provider.TryGet($"{path}:{field}", out var value)) return previous ?? AiCapabilitySupport.Unknown;
        if (!Enum.TryParse<AiCapabilitySupport>(value, ignoreCase: true, out var support) || !Enum.IsDefined(support))
            throw InvalidCompatibilityConfiguration();
        return support;
    }

    private static OptionsValidationException InvalidCompatibilityConfiguration() =>
        new(Options.DefaultName, typeof(ChatGptOptions),
            ["ChatGpt configuration is invalid. Each compatibility layer requires explicit unique exact model IDs and valid capability fields/states."]);

    public bool HasValidCompatibilityProfiles() => CompatibilityProfiles is not null &&
        CompatibilityProfiles.All(profile => profile is not null && profile.IsValid()) &&
        CompatibilityProfiles.Select(profile => profile.ModelId).Distinct(StringComparer.Ordinal).Count() == CompatibilityProfiles.Count;

    public bool IsValid() =>
        Uri.TryCreate(CallbackUri, UriKind.Absolute, out var callback) &&
        callback.Scheme == "http" && callback.Host == "127.0.0.1" &&
        callback.AbsolutePath == CallbackPath && callback.Query.Length == 0 &&
        callback.Fragment.Length == 0 && callback.UserInfo.Length == 0 &&
        Path.IsPathFullyQualified(DataDirectory);
}
