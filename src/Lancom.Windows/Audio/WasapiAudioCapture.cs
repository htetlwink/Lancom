using NAudio.CoreAudioApi;
using NAudio.Wave;
using Lancom.Core.Audio;

namespace Lancom.Windows.Audio;

public class WasapiAudioCapture : IDisposable
{
    private WasapiCapture? _capture;
    private bool _isCapturing;
    private readonly List<short> _sampleAccumulator = new();
    private readonly object _accumulatorLock = new();

    public event Action<byte[]>? OnAudioCaptured;
    public bool IsCapturing => _isCapturing;

    public void Start(MMDevice? device = null)
    {
        if (_isCapturing) return;

        try
        {
            _capture = device != null ? new WasapiCapture(device) : new WasapiCapture();
            _capture.ShareMode = AudioClientShareMode.Shared;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (s, e) => _isCapturing = false;

            ResetAccumulator();
            _capture.StartRecording();
            _isCapturing = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WASAPI Capture Init Error] {ex.Message}");
            _isCapturing = false;
        }
    }

    public void ResetAccumulator()
    {
        lock (_accumulatorLock)
        {
            _sampleAccumulator.Clear();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _capture == null) return;

        var fmt = _capture.WaveFormat;
        short[] mono48kSamples = ConvertToMono48k(e.Buffer, e.BytesRecorded, fmt);
        if (mono48kSamples.Length == 0) return;

        lock (_accumulatorLock)
        {
            _sampleAccumulator.AddRange(mono48kSamples);

            // Drain exact 20ms frames (AudioConfig.SamplesPerFrame = 960 samples / 1920 bytes)
            while (_sampleAccumulator.Count >= AudioConfig.SamplesPerFrame)
            {
                short[] frameShorts = new short[AudioConfig.SamplesPerFrame];
                _sampleAccumulator.CopyTo(0, frameShorts, 0, AudioConfig.SamplesPerFrame);
                _sampleAccumulator.RemoveRange(0, AudioConfig.SamplesPerFrame);

                byte[] frameBytes = ConvertShortsToBytes(frameShorts);
                OnAudioCaptured?.Invoke(frameBytes);
            }
        }
    }

    private static short[] ConvertToMono48k(byte[] input, int length, WaveFormat srcFmt)
    {
        int channels = Math.Max(1, srcFmt.Channels);
        int bitsPerSample = srcFmt.BitsPerSample;
        int bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample <= 0) bytesPerSample = 2;

        int frameBytes = channels * bytesPerSample;
        int totalInputFrames = length / frameBytes;
        if (totalInputFrames <= 0) return Array.Empty<short>();

        // Detect if audio data is IEEE 32-bit floating point
        bool isFloat = false;
        if (srcFmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            isFloat = true;
        }
        else if (srcFmt is WaveFormatExtensible wfe)
        {
            if (wfe.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ||
                wfe.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))
            {
                isFloat = true;
            }
        }
        // In Windows WASAPI Shared mode, 32-bit capture is virtually always IEEE float
        if (!isFloat && bitsPerSample == 32)
        {
            isFloat = true;
        }

        short[] monoSamples = new short[totalInputFrames];

        if (isFloat && bytesPerSample == 4)
        {
            for (int i = 0; i < totalInputFrames; i++)
            {
                int frameOffset = i * frameBytes;
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    float floatVal = BitConverter.ToSingle(input, frameOffset + ch * 4);
                    if (!float.IsNaN(floatVal) && !float.IsInfinity(floatVal))
                    {
                        sum += floatVal;
                    }
                }
                float monoFloat = sum / channels;
                monoSamples[i] = (short)Math.Clamp((int)(monoFloat * 32767.0f), short.MinValue, short.MaxValue);
            }
        }
        else if (!isFloat && bytesPerSample == 2) // 16-bit PCM
        {
            for (int i = 0; i < totalInputFrames; i++)
            {
                int frameOffset = i * frameBytes;
                int sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    short val = (short)(input[frameOffset + ch * 2] | (input[frameOffset + ch * 2 + 1] << 8));
                    sum += val;
                }
                monoSamples[i] = (short)(sum / channels);
            }
        }
        else if (!isFloat && bytesPerSample == 3) // 24-bit PCM
        {
            for (int i = 0; i < totalInputFrames; i++)
            {
                int frameOffset = i * frameBytes;
                int sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int sampleOffset = frameOffset + ch * 3;
                    int val24 = (input[sampleOffset] << 8) | (input[sampleOffset + 1] << 16) | (input[sampleOffset + 2] << 24);
                    sum += (val24 >> 16);
                }
                monoSamples[i] = (short)(sum / channels);
            }
        }
        else if (!isFloat && bytesPerSample == 4) // 32-bit PCM integer
        {
            for (int i = 0; i < totalInputFrames; i++)
            {
                int frameOffset = i * frameBytes;
                long sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int val32 = BitConverter.ToInt32(input, frameOffset + ch * 4);
                    sum += (val32 >> 16);
                }
                monoSamples[i] = (short)(sum / channels);
            }
        }
        else
        {
            // Fallback: 16-bit PCM read
            for (int i = 0; i < totalInputFrames; i++)
            {
                int frameOffset = i * frameBytes;
                short val = (short)(input[frameOffset] | (input[frameOffset + 1] << 8));
                monoSamples[i] = val;
            }
        }

        // Resample to AudioConfig.SampleRate (48 kHz) if needed
        if (srcFmt.SampleRate == AudioConfig.SampleRate)
        {
            return monoSamples;
        }

        double ratio = (double)srcFmt.SampleRate / AudioConfig.SampleRate;
        int dstCount = (int)(totalInputFrames / ratio);
        if (dstCount <= 0) return Array.Empty<short>();

        short[] dstSamples = new short[dstCount];
        for (int i = 0; i < dstCount; i++)
        {
            double srcIdx = i * ratio;
            int idxFloor = (int)srcIdx;
            int idxCeil = Math.Min(idxFloor + 1, totalInputFrames - 1);
            double frac = srcIdx - idxFloor;

            double s1 = monoSamples[idxFloor];
            double s2 = monoSamples[idxCeil];
            dstSamples[i] = (short)(s1 + (s2 - s1) * frac);
        }

        return dstSamples;
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

    public void Stop()
    {
        if (!_isCapturing) return;
        _isCapturing = false;
        ResetAccumulator();
        _capture?.StopRecording();
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _capture = null;
    }
}
