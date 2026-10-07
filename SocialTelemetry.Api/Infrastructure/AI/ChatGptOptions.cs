using System.ComponentModel.DataAnnotations;
using SocialTelemetry.Api.Infrastructure.Runtime;

namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ChatGptOptions
{
    public const string CallbackPath = "/ai-connection/chatgpt/callback";
    public string CallbackUri { get; set; } = "http://127.0.0.1:5059" + CallbackPath;
    public string DataDirectory { get; set; } = ApplicationPaths.DefaultCredentialDirectory("ChatGpt");

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

    public bool IsValid() =>
        Uri.TryCreate(CallbackUri, UriKind.Absolute, out var callback) &&
        callback.Scheme == "http" && callback.Host == "127.0.0.1" &&
        callback.AbsolutePath == CallbackPath && callback.Query.Length == 0 &&
        callback.Fragment.Length == 0 && callback.UserInfo.Length == 0 &&
        Path.IsPathFullyQualified(DataDirectory);
}
