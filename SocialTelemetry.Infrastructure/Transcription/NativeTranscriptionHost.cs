using System.Reflection;
using System.Runtime.InteropServices;
using SocialTelemetry.Application.Contracts.Transcription;

namespace SocialTelemetry.Infrastructure.Transcription;

// Composition supplies the real executable host, never a request or a provider setting.
public sealed class NativeTranscriptionHost
{
    public string Executable { get; }
    public IReadOnlyList<string> PrefixArguments { get; }

    private NativeTranscriptionHost(string executable, params string[] prefixArguments)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || executable.Contains('\0'))
            throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
        Executable = executable;
        PrefixArguments = Array.AsReadOnly((string[])prefixArguments.Clone());
    }

    public static NativeTranscriptionHost FromHostAssembly(Assembly hostAssembly)
    {
        if (hostAssembly.EntryPoint is null)
            throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
        var executable = Environment.ProcessPath;
        if (executable is null)
            throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
        if (Assembly.GetEntryAssembly() == hostAssembly &&
            Path.GetFileNameWithoutExtension(executable) != "dotnet")
            return ForAppHost(executable, hostAssembly);

        // Framework-dependent integration hosts explicitly supply the Api assembly; the
        // test runner's apphost must never become the production launcher executable.
        if (Path.GetFileNameWithoutExtension(executable) != "dotnet")
            executable = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
                "../../../", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return ForDotnet(executable, hostAssembly);
    }

    public static NativeTranscriptionHost ForDotnet(string dotnetExecutable, Assembly hostAssembly)
    {
        var assemblyPath = hostAssembly.Location;
        if (hostAssembly.EntryPoint is null || !Path.IsPathFullyQualified(assemblyPath) || !File.Exists(assemblyPath))
            throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
        return new NativeTranscriptionHost(dotnetExecutable, assemblyPath);
    }

    public static NativeTranscriptionHost ForAppHost(string appHostExecutable, Assembly hostAssembly)
    {
        if (hostAssembly.EntryPoint is null || Path.GetExtension(appHostExecutable).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
        return new NativeTranscriptionHost(appHostExecutable);
    }

    internal TranscriptionCommand LauncherCommand(TranscriptionCommand command) => command with
    {
        Executable = Executable,
        Arguments = PrefixArguments.Concat([NativeTranscriptionBootstrap.LauncherArgument, command.Executable])
            .Concat(command.Arguments).ToArray()
    };
}
