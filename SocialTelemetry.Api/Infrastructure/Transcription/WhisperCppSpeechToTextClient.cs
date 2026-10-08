using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using SocialTelemetry.Api.Infrastructure.Runtime;
using SocialTelemetry.Api.Infrastructure.Storage;

namespace SocialTelemetry.Api.Infrastructure.Transcription;

public sealed class WhisperCppSpeechToTextClient(IOptions<WhisperCppOptions> options,
    ITranscriptionProcessRunner processes, ApplicationPaths paths, IOptions<AttachmentStorageOptions> attachments,
    IOptions<ProfileStorageOptions> profiles, ILogger<WhisperCppSpeechToTextClient> logger) : ISpeechToTextClient, IDisposable
{
    private readonly SemaphoreSlim slots = new(options.Value.Concurrency);

    public async Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        var ffmpeg = ValidateFile(settings.FfmpegExecutable, executable: true);
        var whisper = ValidateFile(settings.WhisperExecutable, executable: true);
        var model = ValidateFile(settings.ModelFile, executable: false);
        string root;
        try
        {
            root = paths.TranscriptionTemporaryDirectory(settings.TemporaryDirectory);
            ValidateTemporaryRoot(root);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var acquired = false;
        string? working = null;
        var ioFailure = SpeechToTextFailure.DecoderFailed;
        try
        {
            // Do not queue requests holding audio buffers behind a long-running native job.
            if (!await slots.WaitAsync(0, timeout.Token)) throw new SpeechToTextException(SpeechToTextFailure.Busy);
            acquired = true;
            working = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(working);
            var input = Path.Combine(working, "source.audio");
            await CopyInputAsync(audio, input, settings.InputBytes, timeout.Token);
            if (!HasSupportedSignature(input)) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
            var decoded = Path.Combine(working, "decoded.wav");
            // Decode one extra second only to detect/reject over-duration inputs, never return a truncated transcript.
            string[] decoderArguments = ["-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-y",
                "-protocol_whitelist", "file", "-format_whitelist", "wav,mp3,mov,aac,ogg,matroska,webm",
                "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn", "-ar", "16000", "-ac", "1",
                "-c:a", "pcm_s16le", "-t", (settings.DurationSeconds + 1).ToString(CultureInfo.InvariantCulture),
                "-fs", settings.DecodedBytes.ToString(CultureInfo.InvariantCulture), "-f", "wav", decoded];
            if (await processes.RunAsync(new(ffmpeg, working, decoderArguments, decoded, settings.DecodedBytes), timeout.Token) != 0)
                throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
            PcmWaveValidation.Validate(decoded, settings);
            ioFailure = SpeechToTextFailure.TranscriptionFailed;

            var outputPrefix = Path.Combine(working, "transcript");
            var output = outputPrefix + ".txt";
            string[] whisperArguments = ["--model", model, "--file", decoded, "--language", "auto",
                "--output-txt", "--output-file", outputPrefix, "--no-prints", "--no-timestamps"];
            if (await processes.RunAsync(new(whisper, working, whisperArguments,
                output, (long)settings.ResultCharacters * 4 + 3), timeout.Token) != 0)
                throw new SpeechToTextException(SpeechToTextFailure.TranscriptionFailed);
            if (!File.Exists(output) || new FileInfo(output).Length > (long)settings.ResultCharacters * 4 + 3)
                throw new SpeechToTextException(SpeechToTextFailure.InvalidResult);
            using var reader = new StreamReader(output, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var text = new StringBuilder();
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                if (text.Length + count > settings.ResultCharacters) throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
                text.Append(buffer, 0, count);
            }
            var result = text.ToString().Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(result)) throw new SpeechToTextException(SpeechToTextFailure.InvalidResult);
            return new(result, "whisper-cpp", settings.ModelId, "whisper-cli-text-v1");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechToTextException(SpeechToTextFailure.TimedOut);
        }
        catch (DecoderFallbackException) { throw new SpeechToTextException(SpeechToTextFailure.InvalidResult); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SpeechToTextException(ioFailure);
        }
        finally
        {
            if (working is not null)
            {
                try { if (Directory.Exists(working)) Directory.Delete(working, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Transcription temporary cleanup failed ({ExceptionType})", exception.GetType().Name);
                }
            }
            if (acquired) slots.Release();
        }
    }

    private static string ValidateFile(string? file, bool executable)
    {
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file) || !File.Exists(file) ||
            (executable && OperatingSystem.IsWindows() && !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
        try { using var stream = File.OpenRead(file); if (stream.Length == 0) throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired); }
        return file;
    }

    private void ValidateTemporaryRoot(string root)
    {
        foreach (var media in new[] { paths.Attachments(attachments.Value), paths.Avatars(profiles.Value, attachments.Value) })
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(media));
            if (temporary.Equals(storage, comparison) || temporary.StartsWith(storage + Path.DirectorySeparatorChar, comparison) ||
                storage.StartsWith(temporary + Path.DirectorySeparatorChar, comparison))
                throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
        }
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (File.Exists(directory.FullName) || (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired);
    }

    private static async Task CopyInputAsync(Stream source, string file, int maximum, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            total += count;
            if (total > maximum) throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        if (total == 0) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
    }

    private static bool HasSupportedSignature(string input)
    {
        using var source = File.OpenRead(input);
        Span<byte> header = stackalloc byte[12];
        var read = source.Read(header);
        return (read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8)) ||
            (read >= 3 && header[..3].SequenceEqual("ID3"u8)) ||
            (read >= 2 && header[0] == 255 && (header[1] & 224) == 224) ||
            (read >= 8 && header[4..8].SequenceEqual("ftyp"u8)) ||
            (read >= 4 && (header[..4].SequenceEqual("OggS"u8) || header[..4].SequenceEqual(new byte[] { 26, 69, 223, 163 })));
    }

    public void Dispose() => slots.Dispose();
}
