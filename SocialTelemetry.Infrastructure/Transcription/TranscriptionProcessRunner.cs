using SocialTelemetry.Application.Contracts.Transcription;
using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Options;

namespace SocialTelemetry.Infrastructure.Transcription;

// A narrow native-process boundary permits deterministic tests without decoder/model binaries.
public interface ITranscriptionProcessRunner
{
    Task<int> RunAsync(TranscriptionCommand command, CancellationToken cancellationToken);
}

public sealed record TranscriptionCommand(string Executable, string WorkingDirectory,
    IReadOnlyList<string> Arguments, string OutputFile, long MaximumFileBytes);

public sealed class TranscriptionProcessRunner(IOptions<WhisperCppOptions> options, NativeTranscriptionHost? host = null) : ITranscriptionProcessRunner
{
    public async Task<int> RunAsync(TranscriptionCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OwnedTranscriptionProcess process;
        try { process = OwnedTranscriptionProcess.Start(command, host); }
        catch (Exception exception) when (exception is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        { throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired); }
        return await RunAsync(command, process, cancellationToken);
    }

    // Exercise teardown/error precedence with an already-owned process, without launching native tools.
    internal async Task<int> RunAsync(TranscriptionCommand command, OwnedTranscriptionProcess process, CancellationToken cancellationToken)
    {
        using (process)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var cleanup = new CancellationTokenSource();
            using var drainStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cleanup.Token);
            var exceeded = 0;
            long characters = 0;
            var output = DrainAsync(standardError: false);
            var errors = DrainAsync(standardError: true);
            var monitor = MonitorFileAsync();
            var drains = Task.WhenAll(output, errors, monitor);
            int exitCode;
            try
            {
                try { exitCode = await process.WaitForExitAsync(options.Value.FileCheckMilliseconds, stop.Token); }
                finally
                {
                    // Root exit is a cleanup trigger, not proof that inherited pipes/descendants exited.
                    // Reuse the configured native timeout as the independent, bounded teardown budget.
                    cleanup.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
                    var cleanupFailed = false;
                    try
                    {
                        try { await process.TerminateAsync(options.Value.FileCheckMilliseconds, cleanup.Token); }
                        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                        { cleanupFailed = true; }
                        catch (Win32Exception) { cleanupFailed = true; }
                        catch (SpeechToTextException exception) when (exception.Failure == SpeechToTextFailure.CleanupFailed)
                        { cleanupFailed = true; }

                        if (!cleanupFailed)
                        {
                            // A completed, faulted drain is an execution failure, not uncertain termination.
                            try { await drains.WaitAsync(cleanup.Token); }
                            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                            { cleanupFailed = true; }
                        }
                    }
                    finally
                    {
                        stop.Cancel();
                        // Reads are nonblocking native polls, so cancellation also releases escaped pipes.
                        try { await drains; }
                        catch (OperationCanceledException) { }
                        // Observe every drain, but never replace an established cleanup uncertainty.
                        catch (Exception) when (cleanupFailed) { }
                    }
                    if (cleanupFailed) throw new SpeechToTextException(SpeechToTextFailure.CleanupFailed);
                }
            }
            catch (OperationCanceledException) when (Volatile.Read(ref exceeded) != 0 && !cancellationToken.IsCancellationRequested)
            { throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded); }
            catch (Win32Exception)
            { throw new SpeechToTextException(SpeechToTextFailure.TranscriptionFailed); }
            if (Volatile.Read(ref exceeded) != 0) throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
            if (File.Exists(command.OutputFile) && new FileInfo(command.OutputFile).Length > command.MaximumFileBytes)
                throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
            cancellationToken.ThrowIfCancellationRequested();
            return exitCode;

            async Task DrainAsync(bool standardError)
            {
                try
                {
                    var buffer = new byte[1024];
                    var decoded = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
                    var decoder = Encoding.UTF8.GetDecoder();
                    int count;
                    while ((count = await process.ReadAsync(standardError, buffer, options.Value.FileCheckMilliseconds, drainStop.Token)) > 0)
                    {
                        // Count decoded characters and discard bytes; never retain native diagnostics.
                        if (Interlocked.Add(ref characters, decoder.GetChars(buffer, 0, count, decoded, 0, flush: false)) > options.Value.ProcessOutputCharacters)
                        {
                            Interlocked.Exchange(ref exceeded, 1);
                            stop.Cancel();
                            return;
                        }
                    }
                    if (Interlocked.Add(ref characters, decoder.GetChars([], 0, 0, decoded, 0, flush: true)) > options.Value.ProcessOutputCharacters)
                    { Interlocked.Exchange(ref exceeded, 1); stop.Cancel(); }
                }
                catch { stop.Cancel(); throw; }
            }

            async Task MonitorFileAsync()
            {
                try
                {
                    while (true)
                    {
                        if (File.Exists(command.OutputFile) && new FileInfo(command.OutputFile).Length > command.MaximumFileBytes)
                        { Interlocked.Exchange(ref exceeded, 1); stop.Cancel(); return; }
                        if (process.TryGetExitCode(out _)) return;
                        await Task.Delay(options.Value.FileCheckMilliseconds, drainStop.Token);
                    }
                }
                catch { stop.Cancel(); throw; }
            }
        }
    }
}
