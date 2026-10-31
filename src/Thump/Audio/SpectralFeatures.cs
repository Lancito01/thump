using NAudio.Dsp;

namespace Thump.Audio;

/// <summary>
/// The "what did that sound like" fingerprint shared by the hit detectors: how much each of 14 bands rose in this
/// hop, relative to that band's own recent peak (so a loud band doesn't drown out a quiet click or rattle).
/// </summary>
public sealed class SpectralFeatures
{
    // Kicks: low thump + click up top; basslines: low-mids and harmonics; claps and snares: mids and upper mids;
    // hats: the top two bands.
    static readonly double[] Edges = [30, 45, 60, 80, 105, 140, 190, 260, 400, 700, 1200, 2500, 5000, 10000, 16000];
    public static int Dims => Edges.Length - 1;

    /// <summary>Index of the first band at or above <paramref name="hz"/>.</summary>
    public static int BandAt(double hz) => Math.Max(0, Array.FindIndex(Edges, e => e >= hz));

    readonly double[] _prevBand = new double[Dims];
    /// <summary>The whole mix's level (summed band magnitudes), smoothed over about a second.</summary>
    public double MixLevel { get; private set; }
    /// <summary>
    /// How much the whole spectrum rose in the latest hop (each band relative to its own recent peak, summed): a
    /// general "something just started" signal, whatever the instrument, for following the tempo.
    /// </summary>
    public double Flux { get; private set; }
    readonly double[] _bandPeak = new double[Dims];

    /// <summary>Per-band rise in the latest hop (not normalised).</summary>
    public double[] Current { get; private set; } = new double[Dims];
    /// <summary>The same for the hop before, which is where the detectors look for onset peaks.</summary>
    public double[] Previous { get; private set; } = new double[Dims];
    /// <summary>Per-band rise in the hop before, in absolute magnitude per bin: the actual spectral shape of the hit.</summary>
    public double[] PreviousAbsolute { get; private set; } = new double[Dims];
    double[] _currentAbsolute = new double[Dims];

    /// <summary>
    /// Share of the previous hop's rise that sits in 400 Hz–2.5 kHz rather than 5–16 kHz. Claps (hand noise around
    /// 1–2 kHz) and snares (the shell and the lower wire noise) have plenty there; hi-hats and cymbals have next to none.
    /// </summary>
    public double MidShare()
    {
        double mid = 0, high = 0;
        for (int d = BandAt(400); d < BandAt(2500); d++) mid += PreviousAbsolute[d];
        for (int d = BandAt(5000); d < Dims; d++) high += PreviousAbsolute[d];
        return mid + high > 1e-12 ? mid / (mid + high) : 0.5;
    }

    /// <summary>
    /// How kick-like the previous hop's low end is: whether its rise sits in the kick's 45–140 Hz rather than a tom's or
    /// bass pluck's 140–400 Hz or a sub swell's 30–45 Hz, and whether it punches out of the mix around it. Log-odds
    /// evidence, roughly −1 (a bass note or swell) to +0.55 (a kick in a drop).
    /// </summary>
    public double KickShape()
    {
        double kick = 0, low = 0;
        for (int d = 0; d < BandAt(400); d++)
        {
            low += PreviousAbsolute[d];
            if (d >= BandAt(45) && d < BandAt(140)) kick += PreviousAbsolute[d];
        }
        if (low <= 1e-12 || MixLevel <= 1e-12) return 0;
        double pitch = Math.Clamp(1.5 * (kick / low - 0.75), -0.6, 0.25);
        double punch = Math.Clamp(0.6 * Math.Log(kick / MixLevel / 0.2), -0.6, 0.3);
        return pitch + punch;
    }

    public void Compute(Complex[] fft, int fftSize, double binHz, double dt)
    {
        (Previous, Current) = (Current, Previous);
        (PreviousAbsolute, _currentAbsolute) = (_currentAbsolute, PreviousAbsolute);
        double decay = Math.Exp(-dt / 8), mix = 0, flux = 0;
        for (int d = 0; d < Dims; d++)
        {
            int a = Math.Max(1, (int)Math.Floor(Edges[d] / binHz));
            int z = Math.Min(fftSize / 2 - 1, Math.Max(a, (int)Math.Ceiling(Edges[d + 1] / binHz) - 1));
            double m = 0;
            for (int i = a; i <= z; i++) m += Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
            m /= z - a + 1;
            _bandPeak[d] = Math.Max(m, _bandPeak[d] * decay);
            _currentAbsolute[d] = Math.Max(0, m - _prevBand[d]);
            Current[d] = _bandPeak[d] > 1e-9 ? _currentAbsolute[d] / _bandPeak[d] : 0;
            _prevBand[d] = m;
            mix += m;
            flux += Current[d];
        }
        Flux = flux;
        MixLevel += (mix - MixLevel) * (1 - Math.Exp(-dt / 1.0));
    }
}
