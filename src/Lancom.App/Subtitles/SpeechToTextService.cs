using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Lancom.Core.Audio;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

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
    private static bool _resolverRegistered;

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
            string lancomBaseDir = Path.Combine(localAppData, "Lancom");
            string targetDir = Path.Combine(lancomBaseDir, "models");
            string targetPath = Path.Combine(targetDir, "ggml-tiny.bin");

            // Ensure native whisper runtime binaries are extracted to %LOCALAPPDATA%\Lancom\runtimes\win-x64
            EnsureNativeRuntimesExtracted(lancomBaseDir);

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
        catch (DllNotFoundException dnfEx)
        {
            IsModelLoaded = false;
            ModelStatus = $"Missing VC++ Runtime: {dnfEx.Message}. Install Microsoft Visual C++ 2015-2022 Redistributable (x64).";
            Debug.WriteLine($"[SpeechToTextService] DLL load failure: {dnfEx}");
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

    private static void EnsureNativeRuntimesExtracted(string lancomBaseDir)
    {
        string runtimesWinX64 = Path.Combine(lancomBaseDir, "runtimes", "win-x64");
        Directory.CreateDirectory(runtimesWinX64);

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string appRuntimes = Path.Combine(appDir, "runtimes", "win-x64");

        var assembly = typeof(SpeechToTextService).Assembly;
        string[] files = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll"];

        foreach (var file in files)
        {
            string targetPath = Path.Combine(runtimesWinX64, file);
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
            {
                string localAppFile = Path.Combine(appRuntimes, file);
                if (File.Exists(localAppFile))
                {
                    File.Copy(localAppFile, targetPath, overwrite: true);
                }
                else
                {
                    string resourceName = $"Lancom.App.runtimes.win_x64.{file}";
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var fileStream = File.Create(targetPath);
                        stream.CopyTo(fileStream);
                    }
                }
            }
        }

        // Determine which directory has valid whisper binaries
        string chosenDir = runtimesWinX64;
        if (File.Exists(Path.Combine(appRuntimes, "whisper.dll")))
        {
            chosenDir = appRuntimes;
        }

        // Pre-load native dependencies in exact dependency order so Windows and Whisper.net resolve them
        string[] loadOrder = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll"];
        IntPtr whisperHandle = IntPtr.Zero;

        foreach (var file in loadOrder)
        {
            string filePath = Path.Combine(chosenDir, file);
            if (File.Exists(filePath))
            {
                var handle = NativeLibrary.Load(filePath);
                if (file == "whisper.dll")
                {
                    whisperHandle = handle;
                }
            }
        }

        if (whisperHandle != IntPtr.Zero)
        {
            lock (typeof(SpeechToTextService))
            {
                if (!_resolverRegistered)
                {
                    NativeLibrary.SetDllImportResolver(typeof(WhisperFactory).Assembly, (libraryName, asm, searchPath) =>
                    {
                        if (libraryName == "whisper" || libraryName == "libwhisper" || libraryName == "whisper.dll")
                        {
                            return whisperHandle;
                        }
                        return IntPtr.Zero;
                    });
                    _resolverRegistered = true;
                }
            }

            // Tell Whisper.net that the library is already loaded into process memory
            RuntimeOptions.LoadedLibrary = RuntimeLibrary.Cpu;
        }

        // Also configure default search paths as standard fallback
        RuntimeOptions.LibraryPath = Path.Combine(lancomBaseDir, "runtimes.sentinel");
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
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
