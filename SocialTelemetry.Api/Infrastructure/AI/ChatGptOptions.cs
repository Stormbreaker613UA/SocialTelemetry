namespace SocialTelemetry.Api.Infrastructure.AI;

public sealed class ChatGptOptions
{
    public const string CallbackPath = "/ai-connection/chatgpt/callback";
    public string CallbackUri { get; set; } = "http://127.0.0.1:5059" + CallbackPath;
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocialTelemetry", "ChatGpt");

    public bool IsValid() =>
        Uri.TryCreate(CallbackUri, UriKind.Absolute, out var callback) &&
        callback.Scheme == "http" && callback.Host == "127.0.0.1" &&
        callback.AbsolutePath == CallbackPath && callback.Query.Length == 0 &&
        callback.Fragment.Length == 0 && callback.UserInfo.Length == 0 &&
        Path.IsPathFullyQualified(DataDirectory);
}
