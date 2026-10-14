using Lancom.Core.Audio;

namespace Lancom.Core.Dsp;

public enum RogerBeepType
{
    None,
    MotorolaTactical,
    NasaQuindar,
    ShortChirp
}

public static class SfxGenerator
{
    public static byte[] GenerateRogerBeep(RogerBeepType type, float volume = 0.45f)
    {
        return type switch
        {
            RogerBeepType.MotorolaTactical => GenerateDualTone(1200, 60, 1600, 60, volume),
            RogerBeepType.NasaQuindar => GenerateSingleTone(2525, 120, volume),
            RogerBeepType.ShortChirp => GenerateSingleTone(1500, 50, volume),
            _ => Array.Empty<byte>()
        };
    }

    public static byte[] GenerateMicClick(float volume = 0.35f)
    {
        // 40ms mechanical relay chirp: 1800Hz (20ms) then 2400Hz (20ms)
        return GenerateDualTone(1800, 20, 2400, 20, volume);
    }

    public static byte[] GenerateSquelchTail(float volume = 0.25f, int durationMs = 35)
    {
        int totalSamples = AudioConfig.SampleRate * durationMs / 1000;
        short[] pcm = new short[totalSamples];
        Random rng = new(42);

        for (int i = 0; i < totalSamples; i++)
        {
            // White noise between -1.0 and 1.0
            float white = (float)(rng.NextDouble() * 2.0 - 1.0);

            // Exponential decay envelope
            float progress = (float)i / totalSamples;
            float envelope = (float)Math.Exp(-3.5 * progress);

            // Low-pass smooth
            float sample = white * envelope * volume;
            pcm[i] = (short)Math.Clamp((int)(sample * 32767.0f), short.MinValue, short.MaxValue);
        }

        return ConvertShortsToBytes(pcm);
    }

    public static byte[] GenerateBusyTone(float volume = 0.5f)
    {
        // 400Hz double-pulse bonk: 80ms tone, 50ms silence, 80ms tone
        byte[] pulse1 = GenerateSingleTone(400, 80, volume);
        byte[] silence = new byte[AudioConfig.SampleRate * 50 / 1000 * 2];
        byte[] pulse2 = GenerateSingleTone(400, 80, volume);

        byte[] result = new byte[pulse1.Length + silence.Length + pulse2.Length];
        Buffer.BlockCopy(pulse1, 0, result, 0, pulse1.Length);
        Buffer.BlockCopy(silence, 0, result, pulse1.Length, silence.Length);
        Buffer.BlockCopy(pulse2, 0, result, pulse1.Length + silence.Length, pulse2.Length);
        return result;
    }

    public static byte[] GeneratePagerChime(float volume = 0.5f)
    {
        // Tactical two-tone pager chime: 880Hz for 90ms, 40ms silence, 1320Hz for 140ms with smooth 5ms fade-in/fade-out
        byte[] tone1 = GenerateSingleTone(880, 90, volume);
        byte[] silence = new byte[AudioConfig.SampleRate * 40 / 1000 * 2];
        byte[] tone2 = GenerateSingleTone(1320, 140, volume);

        byte[] result = new byte[tone1.Length + silence.Length + tone2.Length];
        Buffer.BlockCopy(tone1, 0, result, 0, tone1.Length);
        Buffer.BlockCopy(silence, 0, result, tone1.Length, silence.Length);
        Buffer.BlockCopy(tone2, 0, result, tone1.Length + silence.Length, tone2.Length);
        return result;
    }

    private static byte[] GenerateSingleTone(double frequency, int durationMs, float volume)
    {
        int sampleCount = AudioConfig.SampleRate * durationMs / 1000;
        short[] pcm = new short[sampleCount];
        double omega = 2.0 * Math.PI * frequency / AudioConfig.SampleRate;

        // Apply 5ms fade-in and fade-out to prevent clicks
        int fadeSamples = Math.Min(sampleCount / 4, AudioConfig.SampleRate * 5 / 1000);

        for (int i = 0; i < sampleCount; i++)
        {
            double raw = Math.Sin(omega * i);

            // Envelope calculation
            double env = 1.0;
            if (i < fadeSamples) env = (double)i / fadeSamples;
            else if (i > sampleCount - fadeSamples) env = (double)(sampleCount - i) / fadeSamples;

            float sample = (float)(raw * env * volume);
            pcm[i] = (short)Math.Clamp((int)(sample * 32767.0), short.MinValue, short.MaxValue);
        }

        return ConvertShortsToBytes(pcm);
    }

    private static byte[] GenerateDualTone(double freq1, int durMs1, double freq2, int durMs2, float volume)
    {
        byte[] tone1 = GenerateSingleTone(freq1, durMs1, volume);
        byte[] tone2 = GenerateSingleTone(freq2, durMs2, volume);

        byte[] combined = new byte[tone1.Length + tone2.Length];
        Buffer.BlockCopy(tone1, 0, combined, 0, tone1.Length);
        Buffer.BlockCopy(tone2, 0, combined, tone1.Length, tone2.Length);
        return combined;
    }

    private static byte[] ConvertShortsToBytes(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return bytes;
    }
}
