using System.Buffers.Binary;

namespace Beutl.Extensions.Voice.Services;

internal readonly record struct VoiceWaveFormat(int Tag, int Channels, int SampleRate, int BitsPerSample, int DataOffset, int DataLength)
{
    public bool IsPcm16 => Tag == 1 && BitsPerSample == 16 && Channels > 0;
}

// The RIFF WAVE files that engines return, read only as far as the speech needs.
internal static class VoiceWave
{
    // About -66 dBFS: quieter than speech starts, but above the ripple a resampler leaves ahead of a sound.
    private const int SilenceThreshold = 16;
    private const double MaxSilenceSeconds = 60;

    public static bool TryReadFormat(ReadOnlySpan<byte> wave, out VoiceWaveFormat format)
    {
        format = default;
        if (wave.Length < 12 || !wave[..4].SequenceEqual("RIFF"u8) || !wave.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        int tag = 0, channels = 0, rate = 0, bits = 0;
        long position = 12;
        while (position + 8 <= wave.Length)
        {
            var id = wave.Slice((int)position, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice((int)position + 4, 4));
            int body = (int)position + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= wave.Length)
            {
                tag = BinaryPrimitives.ReadUInt16LittleEndian(wave.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wave.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(wave.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wave.Slice(body + 14, 2));
                // WAVE_FORMAT_EXTENSIBLE names the sample format in the first bytes of its subformat GUID.
                if (tag == 0xFFFE && size >= 40 && body + 26 <= wave.Length)
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(wave.Slice(body + 24, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (rate <= 0 || channels <= 0) return false;
                // Streaming writers leave the size unset, so the data runs to the end of the file.
                format = new(tag, channels, rate, bits, body, (int)Math.Min(size, (uint)(wave.Length - body)));
                return true;
            }
            position = body + (long)size + (size & 1);
        }
        return false;
    }

    public static bool IsWave(ReadOnlySpan<byte> wave) => TryReadFormat(wave, out _);

    public static int? ReadSampleRate(string path)
    {
        try { return TryReadFormat(File.ReadAllBytes(path), out var format) ? format.SampleRate : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Replaces the silence at either end of 16-bit PCM speech with the given lengths, so that engines that pad their
    // speech themselves start and end it as the other engines do. Other formats are returned unchanged.
    public static byte[] AdjustSilence(byte[] wave, double leading, double trailing)
    {
        if (!TryReadFormat(wave, out var format) || !format.IsPcm16) return wave;
        int frameSize = format.Channels * 2;
        var data = wave.AsSpan(format.DataOffset, format.DataLength - format.DataLength % frameSize);
        int frames = data.Length / frameSize;
        int first = 0;
        while (first < frames && IsSilent(data, first, frameSize)) first++;
        // Nothing audible: there is no speech to place the silence around.
        if (first == frames) return wave;
        int last = frames - 1;
        while (IsSilent(data, last, frameSize)) last--;
        long leadingBytes = (long)Frames(leading, format.SampleRate) * frameSize;
        long trailingBytes = (long)Frames(trailing, format.SampleRate) * frameSize;
        int body = (last - first + 1) * frameSize;
        long length = leadingBytes + body + trailingBytes;
        if (length > int.MaxValue - 44) return wave;
        var result = new byte[44 + length];
        WriteHeader(result, format.Channels, format.SampleRate, (int)length);
        data.Slice(first * frameSize, body).CopyTo(result.AsSpan(44 + (int)leadingBytes));
        return result;
    }

    // Converts 16-bit PCM to the sample rate with the cubic through four samples, as Beutl's resampling effect does
    // with Lagrange, so that a preview sounds as the timeline will. Before and after the speech is silence.
    public static byte[] ResampleLagrange(byte[] wave, int sampleRate)
    {
        if (!TryReadFormat(wave, out var format) || !format.IsPcm16 || sampleRate <= 0 || format.SampleRate == sampleRate) return wave;
        int channels = format.Channels;
        var input = wave.AsSpan(format.DataOffset, format.DataLength - format.DataLength % (channels * 2));
        long frames = input.Length / (channels * 2);
        long outputFrames = (long)Math.Ceiling(frames * (double)sampleRate / format.SampleRate);
        long length = outputFrames * channels * 2;
        if (length > int.MaxValue - 44) return wave;
        var result = new byte[44 + length];
        WriteHeader(result, channels, sampleRate, (int)length);
        var output = result.AsSpan(44);
        for (long i = 0; i < outputFrames; i++)
        {
            double at = i * (double)format.SampleRate / sampleRate;
            long whole = (long)Math.Floor(at);
            double f = at - whole;
            double w0 = -f * (f - 1) * (f - 2) / 6, w1 = (f + 1) * (f - 1) * (f - 2) / 2;
            double w2 = -(f + 1) * f * (f - 2) / 2, w3 = (f + 1) * f * (f - 1) / 6;
            for (int ch = 0; ch < channels; ch++)
            {
                double value = w0 * Sample(input, whole - 1, ch, channels, frames) + w1 * Sample(input, whole, ch, channels, frames)
                    + w2 * Sample(input, whole + 1, ch, channels, frames) + w3 * Sample(input, whole + 2, ch, channels, frames);
                BinaryPrimitives.WriteInt16LittleEndian(output[(int)((i * channels + ch) * 2)..],
                    (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
            }
        }
        return result;

        static double Sample(ReadOnlySpan<byte> data, long frame, int channel, int channels, long frames) =>
            frame < 0 || frame >= frames ? 0 : BinaryPrimitives.ReadInt16LittleEndian(data[(int)((frame * channels + channel) * 2)..]);
    }

    private static void WriteHeader(Span<byte> header, int channels, int sampleRate, int length)
    {
        int frameSize = channels * 2;
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + length);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * frameSize);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)frameSize);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], length);
    }

    private static bool IsSilent(ReadOnlySpan<byte> data, int frame, int frameSize)
    {
        var samples = data.Slice(frame * frameSize, frameSize);
        for (int i = 0; i < samples.Length; i += 2)
            if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(samples[i..])) > SilenceThreshold) return false;
        return true;
    }

    private static int Frames(double seconds, int sampleRate) =>
        (int)Math.Round(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, MaxSilenceSeconds) * sampleRate);
}
