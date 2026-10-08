using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SocialTelemetry.Api.Infrastructure.Transcription;

// A narrow native-process boundary permits deterministic tests without installed decoder/model binaries.
public interface ITranscriptionProcessRunner
{
    Task<int> RunAsync(TranscriptionCommand command, CancellationToken cancellationToken);
}

public sealed record TranscriptionCommand(string Executable, string WorkingDirectory,
    IReadOnlyList<string> Arguments, string OutputFile, long MaximumFileBytes);

public sealed class TranscriptionProcessRunner(IOptions<WhisperCppOptions> options) : ITranscriptionProcessRunner
{
    public async Task<int> RunAsync(TranscriptionCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = command.WorkingDirectory
            }
        };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
        var exceeded = false;
        var started = false;
        long characters = 0;
        try
        {
            try { if (!process.Start()) throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired); }
            catch (System.ComponentModel.Win32Exception) { throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired); }
            started = true;
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(stop.Token), DrainAsync(process.StandardOutput),
                DrainAsync(process.StandardError), MonitorFileAsync());
            if (exceeded) throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (exceeded && !cancellationToken.IsCancellationRequested)
        {
            throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
        }
        finally
        {
            // Only this explicitly started native process and its descendants are owned here.
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), stop.Token)) > 0)
            {
                // Discard native output, including transcript-like stdout and private stderr paths.
                if (Interlocked.Add(ref characters, count) > options.Value.ProcessOutputCharacters)
                {
                    exceeded = true;
                    stop.Cancel();
                    return;
                }
            }
        }

        async Task MonitorFileAsync()
        {
            while (true)
            {
                if (File.Exists(command.OutputFile) && new FileInfo(command.OutputFile).Length > command.MaximumFileBytes)
                {
                    exceeded = true;
                    stop.Cancel();
                    return;
                }
                if (process.HasExited) return;
                await Task.Delay(options.Value.FileCheckMilliseconds, stop.Token);
            }
        }
    }
}
