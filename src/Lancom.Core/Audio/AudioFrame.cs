namespace Lancom.Core.Audio;

public class AudioFrame
{
    public byte[] PcmData { get; set; }
    public long Timestamp { get; set; }
    public float PeakDb { get; private set; }
    public float RmsVolume { get; private set; }

    public AudioFrame(byte[] pcmData, long timestamp = 0)
    {
        PcmData = pcmData;
        Timestamp = timestamp == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : timestamp;
        CalculateLevels();
    }

    private void CalculateLevels()
    {
        if (PcmData.Length < 2) return;

        double sumSquares = 0;
        short peakSample = 0;
        int sampleCount = PcmData.Length / 2;

        for (int i = 0; i < PcmData.Length; i += 2)
        {
            short sample = (short)(PcmData[i] | (PcmData[i + 1] << 8));
            short absSample = Math.Abs(sample);
            if (absSample > peakSample) peakSample = absSample;
            sumSquares += (double)sample * sample;
        }

        double rms = Math.Sqrt(sumSquares / sampleCount) / 32768.0;
        RmsVolume = (float)Math.Clamp(rms, 0.0, 1.0);

        if (peakSample > 0)
        {
            double db = 20 * Math.Log10((double)peakSample / 32767.0);
            PeakDb = (float)Math.Clamp(db, -60.0, 0.0);
        }
        else
        {
            PeakDb = -60.0f;
        }
    }
}
