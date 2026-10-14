using System.ComponentModel;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SocialTelemetry.Tests")]

namespace SocialTelemetry.Api.Infrastructure.Transcription;

// Only native STT launches use this boundary; remote AI/HTTP services are not owned processes.
internal abstract class OwnedTranscriptionProcess : IDisposable
{
    public static OwnedTranscriptionProcess Start(TranscriptionCommand command)
    {
        if (OperatingSystem.IsWindows()) return WindowsTranscriptionProcess.Start(command);
        if (OperatingSystem.IsLinux()) return LinuxTranscriptionProcess.Start(command);
        throw new Win32Exception(); // Fail closed rather than launch without containment.
    }

    public abstract bool TryGetExitCode(out int exitCode);
    public abstract Task<int> ReadAsync(bool standardError, byte[] buffer, int pollMilliseconds, CancellationToken cancellationToken);
    public abstract Task TerminateAsync(int pollMilliseconds, CancellationToken cancellationToken);
    public abstract void Dispose();

    public async Task<int> WaitForExitAsync(int pollMilliseconds, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetExitCode(out var exitCode)) return exitCode;
            await Task.Delay(pollMilliseconds, cancellationToken);
        }
    }
}
