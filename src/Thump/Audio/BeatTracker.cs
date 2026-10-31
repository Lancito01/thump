namespace Thump.Audio;

/// <summary>
/// Follows the tempo and beat phase from every onset in the music (hats, synths, stabs, vocals' consonants, not just
/// drums), weighting the kicks and claps the detectors are sure about more, and keeps score of how well its
/// predictions hold up. Every onset is compared with where the grid says the nearest 16th note should be: on time
/// raises <see cref="Confidence"/>, late or early nudges the tempo (so speed-ups and slow-downs are followed), and
/// onsets that keep landing between 16ths lower it until the grid is worked out again. Because it doesn't wait for a
/// drum, the tempo is known from the first bars of an intro.
/// <para>The grid only ever informs the detectors ("this sound was on the beat"), weighted by how sure the tempo is;
/// it never decides on its own what is a hit.</para>
/// </summary>
public sealed class BeatTracker
{
    double _period;   // seconds per beat; 0 = no grid yet
    double _anchor;   // time (s) of a beat on the grid
    double _hint;     // a period worth trying first: the previous grid's, or one remembered for this song
    double _lastHit = double.NegativeInfinity;
    double _offGrid; // recent weight of onsets that landed between 16ths
    bool _locked;
    readonly Queue<(double T, double W)> _recent = new();

    // General onsets, peak-picked from the all-band flux
    double _prev1Flux, _prev2Flux;
    readonly Queue<(double Value, double T)> _fluxPeaks = new();
    readonly double[] _scratch = new double[1024];

    /// <summary>0..1: how well recent hits matched the predicted grid.</summary>
    public double Confidence { get; private set; }
    /// <summary>Confident enough to call it locked. Hysteresis keeps it from flickering.</summary>
    public bool Locked => _locked;
    /// <summary>Current tempo in BPM (folded into 90–190), 0 if unknown.</summary>
    public double Bpm => _period > 0 ? FoldBpm(60 / _period) : 0;
    public double Period => _period;
    /// <summary>Bumped whenever the grid is rebuilt; learned rhythm patterns are relative to it and start over.</summary>
    public int GridVersion { get; private set; }

    public void Reset(double hintPeriod = 0)
    {
        _period = 0;
        _hint = hintPeriod;
        Confidence = 0;
        _locked = false;
        _offGrid = 0;
        _lastHit = double.NegativeInfinity;
        _recent.Clear();
        GridVersion++;
    }

    /// <summary>Where <paramref name="t"/> falls on the grid: which 16th of the beat, and whether it's close to one.</summary>
    public bool TryGetSlot(double t, out int slot, out bool onGrid)
    {
        slot = -1;
        onGrid = false;
        if (_period <= 0) return false;
        double pos = (t - _anchor) / _period * 4, n = Math.Round(pos);
        onGrid = Math.Abs(pos - n) < 0.3;
        slot = (int)(((long)n % 4 + 4) % 4);
        return true;
    }

    /// <summary>Called every hop. Without hits to check against, the confidence slowly fades (breakdowns).</summary>
    public void Tick(double t, double dt)
    {
        if (t - _lastHit > 2) Confidence *= Math.Exp(-dt / 20);
        UpdateLock();
    }

    /// <summary>
    /// Called every hop with the all-band <see cref="SpectralFeatures.Flux"/>. Clear onsets (a peak at least half as
    /// strong as the strong ones of the last few seconds) count toward the tempo, the stronger the more.
    /// </summary>
    public void Feed(double flux, double t)
    {
        double cand = _prev1Flux;
        bool isPeak = cand > _prev2Flux && cand >= flux;
        _prev2Flux = _prev1Flux;
        _prev1Flux = flux;
        if (!isPeak || cand < 0.05) return;

        _fluxPeaks.Enqueue((cand, t));
        while (_fluxPeaks.Count > 0 && t - _fluxPeaks.Peek().T > 4) _fluxPeaks.Dequeue();
        if (_fluxPeaks.Count < 8) return;
        int n = 0;
        foreach (var (v, _) in _fluxPeaks) _scratch[n++] = v;
        Array.Sort(_scratch, 0, n);
        double strong = _scratch[(int)(0.9 * (n - 1))];
        if (cand >= 0.5 * strong) Observe(t, 0.5 * Math.Min(1, cand / strong));
    }

    /// <summary>An onset. <paramref name="weight"/>: 1 for a confident kick, less for claps and general onsets.</summary>
    public void Observe(double t, double weight)
    {
        _lastHit = t;
        _recent.Enqueue((t, weight));
        while (_recent.Count > 32 || _recent.Count > 0 && t - _recent.Peek().T > 8) _recent.Dequeue();

        if (_period > 0)
        {
            double pos = (t - _anchor) / _period * 4, n = Math.Round(pos);
            if (n >= 1 && Math.Abs(pos - n) < 0.3)
            {
                // Predicted vs actual: the error drives a small phase-locked loop.
                double err = (pos - n) * _period / 4;
                double beats = Math.Floor(n / 4);
                double old = _period;
                _period = Math.Clamp(_period + 0.3 * weight * err / Math.Max(1, n / 4), 0.25, 1.5);
                _anchor += beats * old + 0.5 * weight * err;
                double quality = Math.Exp(-err * err / (2 * 0.018 * 0.018));
                Confidence += 0.08 * weight * (quality - Confidence);
                _offGrid *= 1 - 0.5 * weight;
            }
            else if (n >= 1)
            {
                Confidence += 0.08 * weight * (0 - Confidence);
                _offGrid += weight;
            }
        }

        if (_period <= 0 || _offGrid >= 2) TryInduce(t);
        UpdateLock();
    }

    void UpdateLock()
    {
        if (!_locked && Confidence >= 0.6) _locked = true;
        else if (_locked && Confidence < 0.3) _locked = false;
    }

    void TryInduce(double now)
    {
        var hits = _recent.ToArray();
        double period = 0;

        // A remembered tempo relocks from a few hits.
        double hint = _hint > 0 ? _hint : _period;
        if (hint > 0 && hits.Length >= 4)
        {
            int fits = 0, n = hits.Length;
            for (int i = n - 4; i < n - 1; i++)
                if (Near(hits[i + 1].T - hits[i].T, hint / 4, 0.012) > 0.6) fits++;
            if (fits == 3 && hits[^1].T - hits[^4].T >= hint * 0.9) period = hint;
        }

        if (period == 0)
        {
            if (hits.Length < 8 || hits[^1].T - hits[0].T < 2) return;
            var (best, fit) = InduceTempo(hits);
            if (fit < 0.6) return;
            period = best;
        }

        // Beat phase: the 16th offset that puts the most (weighted) hits on beats, so the grid is anchored to the
        // downbeat-ish hits and not to whichever off-beat hit happened to come first.
        double reference = hits[^1].T, bestScore = -1, anchor = reference;
        for (int k = 0; k < 4; k++)
        {
            double a = reference - k * period / 4, score = 0;
            foreach (var (t, w) in hits) score += w * Near(t - a, period, 0.02);
            if (score > bestScore) { bestScore = score; anchor = a; }
        }

        bool sameGrid = _period > 0 && Math.Abs(period / _period - 1) < 0.04 && Near(anchor - _anchor, _period, 0.03) > 0.5;
        if (!sameGrid)
        {
            GridVersion++;
            // The hits that built the grid already fit it: start halfway, and let the next few hits decide.
            Confidence = 0.45;
        }
        _period = period;
        _anchor = anchor;
        _hint = 0;
        _offGrid = 0;
    }

    /// <summary>
    /// Finds the beat period that best explains the hits: every pair should be a whole number of 16ths apart, better
    /// a whole number of 8ths, best a whole number of beats (so a steady 174 BPM pulse isn't read as dotted 8ths at
    /// 130). A mild prior toward ~125 BPM settles half/double-tempo ambiguity. Returns the period and the (weighted)
    /// share of consecutive intervals that sit on its 16th grid, give or take 20 ms: onsets of different instruments
    /// peak a hop or so apart.
    /// </summary>
    internal static (double Period, double Fit) InduceTempo((double T, double W)[] hits)
    {
        double bestScore = 0, best = 0;
        for (double p = 0.33; p <= 0.75; p += 0.002) // 80–180 BPM
        {
            double score = 0, sixteenth = p / 4;
            for (int i = 0; i < hits.Length; i++)
            for (int j = i + 1; j < hits.Length && hits[j].T - hits[i].T <= 3; j++)
            {
                double d = hits[j].T - hits[i].T;
                if (d < 0.05) continue; // a kick and a clap together
                score += hits[i].W * hits[j].W * (0.5 * Near(d, sixteenth, 0.012) + Near(d, p / 2, 0.014) + 2 * Near(d, p, 0.015));
            }
            double prior = Math.Log2(p / 0.48);
            score *= Math.Exp(-prior * prior / (2 * 0.5 * 0.5));
            if (score > bestScore) { bestScore = score; best = p; }
        }
        if (best == 0) return (0, 0);

        double onGrid = 0, intervals = 0;
        for (int i = 1; i < hits.Length; i++)
        {
            double d = hits[i].T - hits[i - 1].T;
            if (d < 0.05) continue;
            double w = hits[i].W * hits[i - 1].W;
            intervals += w;
            if (Near(d, best / 4, 0.02) > 0.5) onGrid += w;
        }
        return (best, intervals == 0 ? 0 : onGrid / intervals);
    }

    /// <summary>1 when <paramref name="d"/> is a whole multiple of <paramref name="unit"/>, falling off with the error in seconds.</summary>
    internal static double Near(double d, double unit, double tolerance)
    {
        double err = d - Math.Round(d / unit) * unit;
        return Math.Exp(-err * err / (2 * tolerance * tolerance));
    }

    static double FoldBpm(double bpm)
    {
        while (bpm < 90) bpm *= 2;
        while (bpm > 190) bpm /= 2;
        return Math.Round(bpm);
    }
}
