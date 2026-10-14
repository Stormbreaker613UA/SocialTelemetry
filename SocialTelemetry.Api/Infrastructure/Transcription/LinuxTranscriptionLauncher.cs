using System.Runtime.CompilerServices;

namespace SocialTelemetry.Api.Infrastructure.Transcription;

internal static class LinuxTranscriptionLauncher
{
#pragma warning disable CA2255 // Private subprocess entry, before the normal ASP.NET entry point.
    [ModuleInitializer]
    internal static void RunIfRequested()
    {
        if (!OperatingSystem.IsLinux()) return;
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length < 3 || arguments[1] != "--socialtelemetry-stt-launcher") return;
        LinuxTranscriptionProcess.RunLauncher(arguments[2], arguments.Skip(3).ToArray());
    }
#pragma warning restore CA2255
}
