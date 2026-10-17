using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Lancom.Core.Audio;
using Whisper.net;
using Whisper.net.Ggml;

namespace Lancom.App.Subtitles;

public class TranscriptEventArgs : EventArgs
{
    public string SenderName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public bool IsWhisper { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class SpeechToTextService : IDisposable, IAsyncDisposable
{
    private record TranscriptionWorkItem(string SenderName, byte[] Pcm16kHz, bool IsWhisper, DateTime Timestamp);

    private readonly Channel<TranscriptionWorkItem> _queue = Channel.CreateBounded<TranscriptionWorkItem>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisperProcessor;
    private bool _isDisposed;

    public bool IsEnabled { get; set; } = true;
    public bool IsModelLoaded { get; private set; }
    public string ModelStatus { get; private set; } = "Not Initialized";

    public event EventHandler<TranscriptEventArgs>? OnTranscriptReceived;

    public SpeechToTextService()
    {
        _consumerTask = Task.Run(() => ConsumeTranscriptionQueueAsync(_cts.Token));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsModelLoaded) return;

        try
        {
            ModelStatus = "Checking GGML Tiny model...";

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string targetDir = Path.Combine(localAppData, "Lancom", "models");
            string targetPath = Path.Combine(targetDir, "ggml-tiny.bin");

            string? modelPath = null;
            if (File.Exists(Path.Combine(appDir, "models", "ggml-tiny.bin")))
            {
                modelPath = Path.Combine(appDir, "models", "ggml-tiny.bin");
            }
            else if (File.Exists(Path.Combine(appDir, "ggml-tiny.bin")))
            {
                modelPath = Path.Combine(appDir, "ggml-tiny.bin");
            }
            else if (File.Exists(targetPath))
            {
                modelPath = targetPath;
            }

            if (modelPath == null)
            {
                Directory.CreateDirectory(targetDir);
                ModelStatus = "Downloading GGML Tiny model...";

                string tempPath = targetPath + ".downloading";
                using var httpClient = new System.Net.Http.HttpClient();
                var downloader = new WhisperGgmlDownloader(httpClient);
                using (var downloadStream = await downloader.GetGgmlModelAsync(GgmlType.Tiny, QuantizationType.NoQuantization, cancellationToken).ConfigureAwait(false))
                using (var fileStream = File.Create(tempPath))
                {
                    await downloadStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, targetPath, overwrite: true);
                modelPath = targetPath;
            }

            ModelStatus = "Loading Whisper model...";
            var factoryOptions = new WhisperFactoryOptions
            {
                UseGpu = false // Enforce 100% CPU user mode
            };

            _whisperFactory = WhisperFactory.FromPath(modelPath, factoryOptions);
            _whisperProcessor = _whisperFactory.CreateBuilder()
                .WithLanguage("auto")
                .Build();

            IsModelLoaded = true;
            ModelStatus = "Model Ready";
        }
        catch (OperationCanceledException)
        {
            ModelStatus = "Model initialization cancelled";
        }
        catch (Exception ex)
        {
            IsModelLoaded = false;
            ModelStatus = $"Model Unavailable: {ex.Message}";
            Debug.WriteLine($"[SpeechToTextService] Initialization failure: {ex}");
        }
    }

    public void EnqueueAudioSegment(string senderName, byte[] pcm16Bit48kHz, bool isWhisper)
    {
        if (!IsEnabled || !IsModelLoaded || _whisperProcessor == null || _isDisposed)
        {
            return;
        }

        // Ignore segments under ~250ms (less than 12,000 samples / 24,000 bytes at 48kHz)
        if (pcm16Bit48kHz == null || pcm16Bit48kHz.Length < 24000)
        {
            return;
        }

        try
        {
            byte[] pcm16kHz = AudioResampler.Resample48kTo16k(pcm16Bit48kHz);
            if (pcm16kHz.Length == 0) return;

            var workItem = new TranscriptionWorkItem(senderName, pcm16kHz, isWhisper, DateTime.UtcNow);
            _queue.Writer.TryWrite(workItem);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeechToTextService] Enqueue error: {ex.Message}");
        }
    }

    private async Task ConsumeTranscriptionQueueAsync(CancellationToken cancellationToken)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var item))
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (_whisperProcessor == null || !IsModelLoaded)
                {
                    continue;
                }

                try
                {
                    using var wavStream = AudioResampler.CreateWavStream(item.Pcm16kHz);
                    var sb = new StringBuilder();

                    await foreach (var segment in _whisperProcessor.ProcessAsync(wavStream, cancellationToken).ConfigureAwait(false))
                    {
                        if (!string.IsNullOrWhiteSpace(segment.Text))
                        {
                            sb.Append(segment.Text.Trim()).Append(' ');
                        }
                    }

                    string text = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        OnTranscriptReceived?.Invoke(this, new TranscriptEventArgs
                        {
                            SenderName = item.SenderName,
                            Text = text,
                            IsWhisper = item.IsWhisper,
                            Timestamp = item.Timestamp
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SpeechToTextService] Transcription processing error: {ex}");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        _queue.Writer.TryComplete();

        _whisperProcessor?.Dispose();
        _whisperFactory?.Dispose();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        _queue.Writer.TryComplete();

        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch { }

        if (_whisperProcessor is IAsyncDisposable asyncProcessor)
        {
            await asyncProcessor.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _whisperProcessor?.Dispose();
        }

        _whisperFactory?.Dispose();
        _cts.Dispose();
    }
}
