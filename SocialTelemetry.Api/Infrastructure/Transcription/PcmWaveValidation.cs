using System.Text;

namespace SocialTelemetry.Api.Infrastructure.Transcription;

internal static class PcmWaveValidation
{
    public static void Validate(string file, WhisperCppOptions limits)
    {
        if (!File.Exists(file)) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
        using var reader = new BinaryReader(File.OpenRead(file), Encoding.ASCII);
        var stream = reader.BaseStream;
        if (stream.Length > limits.DecodedBytes) throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
        if (stream.Length < 44 || new string(reader.ReadChars(4)) != "RIFF" || reader.ReadUInt32() != stream.Length - 8 ||
            new string(reader.ReadChars(4)) != "WAVE") throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
        var format = false;
        long samples = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var kind = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var end = stream.Position + size;
            if (end > stream.Length) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
            if (kind == "fmt ")
            {
                if (format || size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 || reader.ReadUInt32() != 16000 ||
                    reader.ReadUInt32() != 32000 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
                format = true;
            }
            if (kind == "data")
            {
                if (samples != 0 || size == 0 || size % 2 != 0) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
                samples = size;
            }
            stream.Position = end + size % 2;
        }
        if (!format || samples == 0 || stream.Position != stream.Length) throw new SpeechToTextException(SpeechToTextFailure.InvalidAudio);
        if (samples > (long)limits.DurationSeconds * 32000) throw new SpeechToTextException(SpeechToTextFailure.LimitExceeded);
    }
}
