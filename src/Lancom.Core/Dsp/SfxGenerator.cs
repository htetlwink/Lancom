using Lancom.Core.Audio;

namespace Lancom.Core.Dsp;

public enum RogerBeepType
{
    None = 0,
    MotorolaTactical = 1,
    NasaQuindar = 2,
    ShortChirp = 3,
    HarrisTriTone = 4,
    Mdc1200Police = 5,
    AviationDoublePip = 6,
    KenwoodTwoTone = 7,
    HamRepeater = 8,
    CyberpunkSweep = 9,
    UscmMarine = 10,
    ImperialStormtrooper = 11,
    ColonialViper = 12,
    StarfleetComm = 13,
    Hal9000 = 14,
    BladeRunnerSpinner = 15
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
            RogerBeepType.HarrisTriTone => GenerateMultiTone(volume, (900, 35), (1350, 35), (1800, 35)),
            RogerBeepType.Mdc1200Police => GenerateMultiTone(volume, (1200, 12), (1800, 12), (1200, 12), (1800, 12), (1200, 10)),
            RogerBeepType.AviationDoublePip => GenerateAviationDoublePip(volume),
            RogerBeepType.KenwoodTwoTone => GenerateDualTone(1550, 50, 1050, 50, volume),
            RogerBeepType.HamRepeater => GenerateSingleTone(1000, 75, volume),
            RogerBeepType.CyberpunkSweep => GenerateFrequencySweep(2600, 800, 70, volume, exponentialDecay: true),
            RogerBeepType.UscmMarine => CombineAudio(GenerateMicClick(volume * 0.7f), GenerateSingleTone(3100, 40, volume)),
            RogerBeepType.ImperialStormtrooper => CombineAudio(GenerateMicClick(volume * 0.7f), GenerateDualTone(880, 30, 1760, 30, volume)),
            RogerBeepType.ColonialViper => GenerateFrequencySweep(1450, 950, 60, volume, exponentialDecay: false),
            RogerBeepType.StarfleetComm => GenerateMultiTone(volume, (1175, 30), (1568, 30), (2093, 35)),
            RogerBeepType.Hal9000 => GenerateHal9000(volume),
            RogerBeepType.BladeRunnerSpinner => GenerateDualTone(440, 30, 1850, 40, volume),
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

    public static byte[] GenerateMessageChime(float volume = 0.45f)
    {
        // Crisp, high-tech tactical double bell: 1046Hz for 60ms, 30ms silence, 1568Hz for 100ms
        byte[] tone1 = GenerateSingleTone(1046, 60, volume);
        byte[] silence = new byte[AudioConfig.SampleRate * 30 / 1000 * 2];
        byte[] tone2 = GenerateSingleTone(1568, 100, volume);

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

    private static byte[] GenerateMultiTone(float volume, params (double freq, int durMs)[] tones)
    {
        int totalLen = 0;
        var buffers = new List<byte[]>(tones.Length);
        foreach (var (freq, durMs) in tones)
        {
            byte[] b = GenerateSingleTone(freq, durMs, volume);
            buffers.Add(b);
            totalLen += b.Length;
        }

        byte[] combined = new byte[totalLen];
        int offset = 0;
        foreach (var b in buffers)
        {
            Buffer.BlockCopy(b, 0, combined, offset, b.Length);
            offset += b.Length;
        }
        return combined;
    }

    private static byte[] GenerateFrequencySweep(double startFreq, double endFreq, int durationMs, float volume, bool exponentialDecay)
    {
        int sampleCount = AudioConfig.SampleRate * durationMs / 1000;
        short[] pcm = new short[sampleCount];
        double currentPhase = 0.0;
        int fadeSamples = Math.Min(sampleCount / 4, AudioConfig.SampleRate * 5 / 1000);

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleCount;
            double freq = startFreq + (endFreq - startFreq) * t;
            currentPhase += 2.0 * Math.PI * freq / AudioConfig.SampleRate;

            double env = 1.0;
            if (i < fadeSamples) env = (double)i / fadeSamples;
            else if (i > sampleCount - fadeSamples) env = (double)(sampleCount - i) / fadeSamples;

            if (exponentialDecay)
            {
                env *= Math.Exp(-2.5 * t);
            }

            float sample = (float)(Math.Sin(currentPhase) * env * volume);
            pcm[i] = (short)Math.Clamp((int)(sample * 32767.0), short.MinValue, short.MaxValue);
        }

        return ConvertShortsToBytes(pcm);
    }

    private static byte[] GenerateAviationDoublePip(float volume)
    {
        byte[] pip1 = GenerateSingleTone(2100, 25, volume);
        byte[] silence = new byte[AudioConfig.SampleRate * 20 / 1000 * 2];
        byte[] pip2 = GenerateSingleTone(2100, 25, volume);

        byte[] result = new byte[pip1.Length + silence.Length + pip2.Length];
        Buffer.BlockCopy(pip1, 0, result, 0, pip1.Length);
        Buffer.BlockCopy(silence, 0, result, pip1.Length, silence.Length);
        Buffer.BlockCopy(pip2, 0, result, pip1.Length + silence.Length, pip2.Length);
        return result;
    }

    private static byte[] GenerateHal9000(float volume)
    {
        int sampleCount = AudioConfig.SampleRate * 130 / 1000;
        short[] pcm = new short[sampleCount];
        double omega = 2.0 * Math.PI * 784.0 / AudioConfig.SampleRate;
        int fadeSamples = Math.Min(sampleCount / 8, AudioConfig.SampleRate * 5 / 1000);

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleCount;
            double env = Math.Exp(-3.0 * t);
            if (i < fadeSamples) env *= (double)i / fadeSamples;

            float sample = (float)(Math.Sin(omega * i) * env * volume);
            pcm[i] = (short)Math.Clamp((int)(sample * 32767.0), short.MinValue, short.MaxValue);
        }

        return ConvertShortsToBytes(pcm);
    }

    private static byte[] CombineAudio(byte[] first, byte[] second)
    {
        byte[] combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
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
