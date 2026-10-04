using System.Diagnostics;

namespace SocialTelemetry.Api.Infrastructure.AI;

public interface ISystemBrowser
{
    void Open(Uri authorizationUri);
}

public sealed class SystemBrowser : ISystemBrowser
{
    public void Open(Uri authorizationUri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            throw new AiProviderException(AiFailure.BrowserUnavailable);
        }
    }
}
