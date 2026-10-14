namespace Lancom.Core.Dsp;

public enum BiquadType
{
    LowPass,
    HighPass,
    BandPass
}

public class BiquadFilter
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    public BiquadFilter(BiquadType type, double sampleRate, double frequency, double q = 0.7071)
    {
        Configure(type, sampleRate, frequency, q);
    }

    public void Configure(BiquadType type, double sampleRate, double frequency, double q)
    {
        double omega = 2.0 * Math.PI * frequency / sampleRate;
        double sinOmega = Math.Sin(omega);
        double cosOmega = Math.Cos(omega);
        double alpha = sinOmega / (2.0 * q);

        double a0;

        switch (type)
        {
            case BiquadType.LowPass:
                _b0 = (1.0 - cosOmega) / 2.0;
                _b1 = 1.0 - cosOmega;
                _b2 = (1.0 - cosOmega) / 2.0;
                a0 = 1.0 + alpha;
                _a1 = -2.0 * cosOmega;
                _a2 = 1.0 - alpha;
                break;

            case BiquadType.HighPass:
                _b0 = (1.0 + cosOmega) / 2.0;
                _b1 = -(1.0 + cosOmega);
                _b2 = (1.0 + cosOmega) / 2.0;
                a0 = 1.0 + alpha;
                _a1 = -2.0 * cosOmega;
                _a2 = 1.0 - alpha;
                break;

            case BiquadType.BandPass:
                _b0 = alpha;
                _b1 = 0.0;
                _b2 = -alpha;
                a0 = 1.0 + alpha;
                _a1 = -2.0 * cosOmega;
                _a2 = 1.0 - alpha;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        // Normalize
        _b0 /= a0;
        _b1 /= a0;
        _b2 /= a0;
        _a1 /= a0;
        _a2 /= a0;
    }

    public float Process(float input)
    {
        if (float.IsNaN(input) || float.IsInfinity(input)) input = 0f;
        double outSample = input * _b0 + _z1;
        if (double.IsNaN(outSample) || double.IsInfinity(outSample))
        {
            _z1 = 0;
            _z2 = 0;
            return 0f;
        }
        _z1 = input * _b1 - _a1 * outSample + _z2;
        _z2 = input * _b2 - _a2 * outSample;
        return (float)outSample;
    }

    public void Reset()
    {
        _z1 = 0;
        _z2 = 0;
    }
}
