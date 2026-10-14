using NAudio.CoreAudioApi;
using NAudio.Wave;
using Lancom.Core.Audio;

namespace Lancom.Windows.Audio;

public class WasapiAudioPlayback : IDisposable
{
    private WasapiOut? _wasapiOut;
    private BufferedWaveProvider? _waveProvider;
    private readonly WaveFormat _format;
    private bool _isPlaying;
    private readonly object _lock = new();

    public float Volume
    {
        get => _wasapiOut?.Volume ?? 1.0f;
        set
        {
            if (_wasapiOut != null)
            {
                _wasapiOut.Volume = Math.Clamp(value, 0.0f, 1.0f);
            }
        }
    }

    public WasapiAudioPlayback()
    {
        _format = new WaveFormat(AudioConfig.SampleRate, AudioConfig.BitsPerSample, AudioConfig.Channels);
    }

    public void Start(MMDevice? device = null)
    {
        lock (_lock)
        {
            if (_isPlaying) return;

            try
            {
                _waveProvider = new BufferedWaveProvider(_format)
                {
                    DiscardOnBufferOverflow = true
                };

                _wasapiOut = device != null 
                    ? new WasapiOut(device, AudioClientShareMode.Shared, false, 20)
                    : new WasapiOut(AudioClientShareMode.Shared, 20);

                _wasapiOut.Init(_waveProvider);
                _wasapiOut.Play();
                _isPlaying = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI Out Init Error] {ex.Message}");
                _isPlaying = false;
            }
        }
    }

    public void PlayPcm(byte[] pcmData)
    {
        lock (_lock)
        {
            if (!_isPlaying || _waveProvider == null) return;

            // Prevent bufferbloat: if backlog exceeds 80ms, clear stale buffer
            if (_waveProvider.BufferedDuration.TotalMilliseconds > 80)
            {
                _waveProvider.ClearBuffer();
            }

            _waveProvider.AddSamples(pcmData, 0, pcmData.Length);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isPlaying) return;
            _isPlaying = false;
            _wasapiOut?.Stop();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            _wasapiOut?.Dispose();
            _wasapiOut = null;
            _waveProvider = null;
        }
    }
}
