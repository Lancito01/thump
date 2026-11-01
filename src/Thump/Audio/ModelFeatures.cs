using NAudio.Dsp;

namespace Thump.Audio;

/// <summary>
/// What the drum model hears each hop: the log magnitude of 64 bands spaced evenly in pitch from 30 Hz to 16 kHz.
/// Deliberately raw (no rise, no normalisation): the model learns what matters, and the exact same numbers are
/// computed live and when exporting training data, so it never sees anything at runtime it wasn't trained on.
/// </summary>
public sealed class ModelFeatures
{
    public const int Bands = 64;
    const double Lo = 30, Hi = 16000;

    public float[] Values { get; } = new float[Bands];

    int[]? _from, _to;
    double _binHz;

    public void Compute(Complex[] fft, int fftSize, double binHz)
    {
        if (_from == null || _binHz != binHz) Layout(fftSize, binHz);
        for (int b = 0; b < Bands; b++)
        {
            double sum = 0;
            for (int i = _from![b]; i <= _to![b]; i++) sum += Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
            Values[b] = (float)Math.Log(1e-7 + sum / (_to[b] - _from[b] + 1));
        }
    }

    // Low bands are narrower than one FFT bin; they share bins with their neighbours rather than being empty.
    void Layout(int fftSize, double binHz)
    {
        _binHz = binHz;
        _from = new int[Bands];
        _to = new int[Bands];
        for (int b = 0; b < Bands; b++)
        {
            double lo = Lo * Math.Pow(Hi / Lo, (double)b / Bands), hi = Lo * Math.Pow(Hi / Lo, (b + 1.0) / Bands);
            int a = Math.Clamp((int)Math.Round(lo / binHz), 1, fftSize / 2 - 1);
            _from[b] = a;
            _to[b] = Math.Clamp((int)Math.Round(hi / binHz) - 1, a, fftSize / 2 - 1);
        }
    }
}
