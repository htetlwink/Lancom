using Concentus;
using Concentus.Enums;
using Lancom.Core.Audio;

namespace Lancom.Core.Codec;

public interface IOpusCodec : IDisposable
{
    byte[] Encode(byte[] pcmData);
    byte[] Decode(byte[]? opusData, bool decodeFec = false);
    void Reset();
}

public class ConcentusOpusCodec : IOpusCodec
{
    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private readonly short[] _encodeBuffer;
    private readonly short[] _decodeBuffer;
    private readonly byte[] _outEncodedBuffer;
    private readonly object _lock = new();
    private bool _disposed;

    public ConcentusOpusCodec(int bitrate = AudioConfig.DefaultBitrate)
    {
        _encoder = OpusCodecFactory.CreateEncoder(AudioConfig.SampleRate, AudioConfig.Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
        _encoder.Complexity = 6;

        _decoder = OpusCodecFactory.CreateDecoder(AudioConfig.SampleRate, AudioConfig.Channels);

        _encodeBuffer = new short[AudioConfig.SamplesPerFrame];
        _decodeBuffer = new short[AudioConfig.SamplesPerFrame];
        _outEncodedBuffer = new byte[AudioConfig.FrameByteSize];
    }

    public byte[] Encode(byte[] pcmData)
    {
        lock (_lock)
        {
            if (_disposed || pcmData.Length < AudioConfig.FrameByteSize) return Array.Empty<byte>();

            int sampleCount = Math.Min(pcmData.Length / 2, _encodeBuffer.Length);
            for (int i = 0; i < sampleCount; i++)
            {
                _encodeBuffer[i] = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            }

            // Using Span-based modern API
            ReadOnlySpan<short> pcmSpan = new(_encodeBuffer, 0, AudioConfig.SamplesPerFrame);
            Span<byte> outSpan = new(_outEncodedBuffer);
            int encodedBytes = _encoder.Encode(pcmSpan, AudioConfig.SamplesPerFrame, outSpan, outSpan.Length);
            if (encodedBytes <= 0) return Array.Empty<byte>();

            byte[] result = new byte[encodedBytes];
            Buffer.BlockCopy(_outEncodedBuffer, 0, result, 0, encodedBytes);
            return result;
        }
    }

    public byte[] Decode(byte[]? opusData, bool decodeFec = false)
    {
        lock (_lock)
        {
            if (_disposed) return Array.Empty<byte>();

            Span<short> decodeSpan = new(_decodeBuffer);
            int samplesDecoded;
            if (opusData == null || opusData.Length == 0)
            {
                samplesDecoded = _decoder.Decode(ReadOnlySpan<byte>.Empty, decodeSpan, AudioConfig.SamplesPerFrame, decodeFec);
            }
            else
            {
                ReadOnlySpan<byte> inSpan = new(opusData);
                samplesDecoded = _decoder.Decode(inSpan, decodeSpan, AudioConfig.SamplesPerFrame, decodeFec);
            }

            if (samplesDecoded <= 0) return Array.Empty<byte>();

            byte[] result = new byte[samplesDecoded * 2];
            for (int i = 0; i < samplesDecoded; i++)
            {
                short sample = _decodeBuffer[i];
                result[i * 2] = (byte)(sample & 0xFF);
                result[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return result;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _encoder.ResetState();
            _decoder.ResetState();
            Array.Clear(_encodeBuffer, 0, _encodeBuffer.Length);
            Array.Clear(_decodeBuffer, 0, _decodeBuffer.Length);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
    }
}
