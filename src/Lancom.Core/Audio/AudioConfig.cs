namespace Lancom.Core.Audio;

public static class AudioConfig
{
    public const int SampleRate = 48000;       // 48 kHz standard
    public const int Channels = 1;             // Mono
    public const int BitsPerSample = 16;       // 16-bit PCM
    public const int BytesPerSample = BitsPerSample / 8; // 2 bytes
    public const int FrameDurationMs = 20;     // 20 ms frame
    public const int SamplesPerFrame = SampleRate * FrameDurationMs / 1000; // 960 samples
    public const int FrameByteSize = SamplesPerFrame * Channels * BytesPerSample; // 1920 bytes
    public const int DefaultBitrate = 24000;   // 24 kbps Opus VoIP mode
}
