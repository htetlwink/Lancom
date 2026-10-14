using Lancom.Core.Audio;

namespace Lancom.Core.Dsp;

public class RadioDsp
{
    private readonly BiquadFilter _rumbleCut;
    private readonly BiquadFilter _bandpassHighPass;
    private readonly BiquadFilter _bandpassLowPass;
    public bool EnableRadioEffect { get; set; } = true;
    public float SaturationDrive { get; set; } = 1.35f;

    public RadioDsp()
    {
        // 80Hz rumble cut (Butterworth Q=0.7071)
        _rumbleCut = new BiquadFilter(BiquadType.HighPass, AudioConfig.SampleRate, 80.0, 0.7071);

        // VHF Radio bandpass (350 Hz to 3200 Hz)
        _bandpassHighPass = new BiquadFilter(BiquadType.HighPass, AudioConfig.SampleRate, 350.0, 0.7071);
        _bandpassLowPass = new BiquadFilter(BiquadType.LowPass, AudioConfig.SampleRate, 3200.0, 0.7071);
    }

    public void ProcessPcm(byte[] pcmData, int offset, int count)
    {
        for (int i = offset; i < offset + count; i += 2)
        {
            short sample = (short)(pcmData[i] | (pcmData[i + 1] << 8));
            float normalized = sample / 32768.0f;

            // Always cut sub-audible mic rumble (<80Hz)
            normalized = _rumbleCut.Process(normalized);

            if (EnableRadioEffect)
            {
                // Narrow bandpass for walkie-talkie spectrum
                normalized = _bandpassHighPass.Process(normalized);
                normalized = _bandpassLowPass.Process(normalized);

                // Analog soft saturation (tanh curve)
                normalized = (float)Math.Tanh(normalized * SaturationDrive);
            }

            // Convert back to 16-bit PCM with clamp
            int intSample = (int)(normalized * 32767.0f);
            short finalSample = (short)Math.Clamp(intSample, short.MinValue, short.MaxValue);

            pcmData[i] = (byte)(finalSample & 0xFF);
            pcmData[i + 1] = (byte)((finalSample >> 8) & 0xFF);
        }
    }

    public void Reset()
    {
        _rumbleCut.Reset();
        _bandpassHighPass.Reset();
        _bandpassLowPass.Reset();
    }
}
