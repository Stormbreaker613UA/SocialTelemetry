using SocialTelemetry.Infrastructure.Platform.Linux;

namespace SocialTelemetry.Infrastructure.Transcription;

public static class NativeTranscriptionBootstrap
{
    public const string LauncherArgument = "--socialtelemetry-stt-launcher";

    // Api calls this before constructing any host/configuration/database/storage services.
    public static int? RunIfRequested(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != LauncherArgument) return null;
        if (!OperatingSystem.IsLinux() || arguments.Length < 2) return 78;
        LinuxTranscriptionProcess.RunLauncher(arguments[1], arguments.Skip(2).ToArray());
        return 0;
    }
}
