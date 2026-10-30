using NAudio.Dsp;

namespace Thump.Audio;

/// <summary>
/// A small per-song learner that decides whether a low-end hit is the kick drum. It learns three things about the
/// track that is playing, starting from scratch on every track change:
/// <list type="bullet">
/// <item><b>Strength</b>: how hard this track's kicks hit. A candidate is scored relative to that, so quiet and loud
/// kicks both work.</item>
/// <item><b>Timbre</b>: a 12-band fingerprint of the kick's attack, plus one of the hits that turned out not to be kicks
/// (bass notes, claps, toms). Each candidate is compared with both: a tiny nearest-template classifier that keeps
/// learning as the track plays.</item>
/// <item><b>Rhythm</b>: the tempo, followed continuously so DJ pitch-riding and tempo ramps don't break it, plus how
/// likely a kick is on each 16th-note slot of the beat (four-on-the-floor, broken beats, …).</item>
/// </list>
/// Everything is O(bins) per FFT hop; no offline training, no model files.
/// </summary>
public sealed class KickDetector
{
    // Timbre bands (Hz). Kicks: low thump + click up top; basslines: low-mids and harmonics; claps: mids.
    static readonly double[] Edges = [30, 45, 60, 80, 105, 140, 190, 260, 400, 700, 1200, 2500, 5000];
    static int Dims => Edges.Length - 1;

    /// <summary>1 = default; higher accepts hits that are less kick-like.</summary>
    public double Sensitivity { get; set; } = 1;
    public double LowHz { get; set; } = 40;
    public double HighHz { get; set; } = 130;

    /// <summary>Current tempo in BPM (folded into 90–190), 0 if unknown.</summary>
    public double Bpm => _period > 0 ? FoldBpm(60 / _period) : 0;
    public bool Locked => _period > 0;
    /// <summary>0..1: how much the detector has learned about this track's kick.</summary>
    public double Confidence => Math.Min(1, _learnedKicks / 16.0) * (_otherCount > 4 ? 1 : 0.7);

    // Diagnostics for tests: (time s, strength / typical, timbre similarity, rhythm prior, score, accepted)
    internal Action<double, double, double, double, double, bool>? Trace;

    // Onset strength (kick band, linear magnitude rise normalised by the band's recent peak)
    double[] _prevKickMag = [];
    double _kickBandPeak;
    double _prev1Strength, _prev2Strength;

    // Timbre features
    readonly double[] _prevBand = new double[Dims];
    readonly double[] _bandPeak = new double[Dims];
    double[] _prev1Feature = new double[Dims];
    double[] _curFeature = new double[Dims];
    readonly double[] _kickTemplate = new double[Dims];
    readonly double[] _otherTemplate = new double[Dims];
    int _otherCount;

    // Strength model
    readonly Queue<(double Value, long Sample)> _peaks = new();
    readonly double[] _scratch = new double[1024];
    double _typicalKick;
    int _learnedKicks;

    // Rhythm model
    long _lastKickSample = long.MinValue / 2;
    long _gridSample;                     // last on-beat kick: the beat grid is anchored here, not to off-beat hits
    double _period;                       // seconds per beat; 0 = not locked
    double _hintPeriod;                   // last locked period, kept through breakdowns
    readonly Queue<double> _recentKicks = new(); // times (s) of the last kicks, for tempo induction
    readonly double[] _slotProb = [0.5, 0.5, 0.5, 0.5]; // P(kick) on each 16th of the beat
    int _strongOffGrid;                   // kick-like hits the grid keeps rejecting → tempo probably changed

    public void Reset()
    {
        _peaks.Clear();
        _typicalKick = 0;
        _learnedKicks = 0;
        Array.Clear(_kickTemplate);
        Array.Clear(_otherTemplate);
        _otherCount = 0;
        _hintPeriod = 0;
        Array.Fill(_slotProb, 0.5);
        Unlock();
    }

    void Unlock()
    {
        if (_period > 0) _hintPeriod = _period;
        _period = 0;
        _recentKicks.Clear();
        _strongOffGrid = 0;
        // The rhythm pattern (_slotProb) survives: after a breakdown or tempo change it's usually the same groove.
    }

    /// <summary>Feed one FFT frame. Returns true if the previous hop was a kick.</summary>
    public bool Process(Complex[] fft, int fftSize, double binHz, double dt, long hopEndSample, int hopSize, int sampleRate)
    {
        double strength = KickBandOnset(fft, fftSize, binHz, dt);
        ComputeTimbre(fft, fftSize, binHz, dt);

        // Peak picking: was the previous hop a local maximum? (one hop ≈ 11 ms of latency)
        double cand = _prev1Strength;
        var feature = _prev1Feature;
        bool isPeak = cand > _prev2Strength && cand >= strength;
        long candSample = hopEndSample - hopSize;
        _prev2Strength = _prev1Strength;
        _prev1Strength = strength;
        (_prev1Feature, _curFeature) = (_curFeature, _prev1Feature);

        double sinceLast = (double)(candSample - _lastKickSample) / sampleRate;
        if (_period > 0 && sinceLast > 4 * _period) Unlock(); // breakdown: the tempo may be different afterwards
        // Breakdown: slowly forget the kick's strength so a softer kick after the drop is found again, but keep the
        // timbre model so the bassline that carries the breakdown isn't mistaken for the kick.
        if (sinceLast > 4) _typicalKick *= Math.Exp(-dt / 6);
        // Very long gap: perhaps a different track without media info. Start over from the clearest hits.
        if (sinceLast > 10) _learnedKicks = Math.Min(_learnedKicks, 3);

        if (!isPeak || cand < MinOnset) return false;
        _peaks.Enqueue((cand, candSample));
        while (_peaks.Count > 0 && candSample - _peaks.Peek().Sample > 4L * sampleRate) _peaks.Dequeue();
        if (_peaks.Count < 8) return false;

        double sens = Math.Max(0.2, Sensitivity);
        bool accept;
        double rel = 0, sim = 0, rhythm = 0.5, score = 0;
        int slot = -1;
        bool onSlot = false;

        if (_learnedKicks < 4)
        {
            // Bootstrapping: nothing learned yet, only the clearest hits count.
            accept = cand >= 0.8 * Percentile(0.9) / sens && sinceLast >= 0.25;
            score = accept ? 1 : 0;
        }
        else
        {
            rel = cand / Math.Max(1e-6, _typicalKick);
            sim = Cosine(feature, _kickTemplate) - (_otherCount > 4 ? Cosine(feature, _otherTemplate) : 0.5);

            if (_period > 0)
            {
                double pos = (double)(candSample - _gridSample) / sampleRate / _period * 4; // 16ths since the last on-beat kick
                double nearest = Math.Round(pos);
                onSlot = Math.Abs(pos - nearest) < 0.3;     // within ±7.5% of a beat
                slot = (int)nearest % 4;
                rhythm = onSlot ? _slotProb[slot] : 0.05;
            }

            score = rel * (1 + sim) * (0.4 + 1.2 * rhythm);
            // Unlocked: kicks closer than 250 ms (240 BPM) are almost always bass notes or flams.
            double refractory = _period > 0 ? 0.45 * _period : 0.25;
            accept = score >= 0.8 / sens && sinceLast >= refractory;

            if (_period > 0 && rel > 0.4 && onSlot)
                _slotProb[slot] = Math.Clamp(_slotProb[slot] + 0.08 * ((accept ? 1 : 0) - _slotProb[slot]), 0.05, 0.98);

            if (!accept && rel > 0.3) Learn(_otherTemplate, feature, _otherCount++ == 0 ? 1 : 0.1);

            if (_period > 0 && !accept && rel >= 0.9 && sim > 0.15)
            {
                // Sounds like our kick, as loud as our kick, but the grid says no. A few of these in a row
                // means the tempo jumped (DJ transition): drop the grid and relock.
                if (++_strongOffGrid >= 4) Unlock();
            }
        }

        Trace?.Invoke((double)candSample / sampleRate, rel, sim, rhythm, score, accept);
        if (!accept) return false;

        _recentKicks.Enqueue((double)candSample / sampleRate);
        if (_recentKicks.Count > 12) _recentKicks.Dequeue();
        if (_period <= 0)
        {
            LearnTempo(sinceLast);
            _gridSample = candSample;
        }
        else if (slot == 0)
        {
            LearnTempo((double)(candSample - _gridSample) / sampleRate);
            _gridSample = candSample;
        }
        Learn(_kickTemplate, feature, _learnedKicks == 0 ? 1 : 0.15);
        _typicalKick = _learnedKicks == 0 ? cand : _typicalKick + 0.15 * (Math.Min(cand, 1.5 * _typicalKick) - _typicalKick);
        _learnedKicks++;
        if (slot == 0) _strongOffGrid = 0;
        _lastKickSample = candSample;
        return true;
    }

    const double MinOnset = 4; // % of the kick band's recent peak

    double KickBandOnset(Complex[] fft, int fftSize, double binHz, double dt)
    {
        int a = Math.Max(1, (int)Math.Floor(LowHz / binHz));
        int z = Math.Min(fftSize / 2 - 1, (int)Math.Ceiling(HighHz / binHz));
        int n = z - a + 1;
        bool first = _prevKickMag.Length != n;
        if (first) _prevKickMag = new double[n];

        // Linear magnitude rise, so within a track a loud kick outranks a quieter bass note (a rise in dB would
        // rank a quiet note out of silence as high as a loud kick). Normalised by the band's recent peak, so it
        // doesn't depend on how loud the track is mastered.
        double rise = 0, total = 0;
        for (int i = 0; i < n; i++)
        {
            var c = fft[a + i];
            double m = Math.Sqrt(c.X * c.X + c.Y * c.Y);
            if (!first) rise += Math.Max(0, m - _prevKickMag[i]);
            total += m;
            _prevKickMag[i] = m;
        }
        _kickBandPeak = Math.Max(total, _kickBandPeak * Math.Exp(-dt / 8));
        return _kickBandPeak > 1e-7 ? 100 * rise / _kickBandPeak : 0; // ≤ 1e-7 ≈ digital silence
    }

    void ComputeTimbre(Complex[] fft, int fftSize, double binHz, double dt)
    {
        double decay = Math.Exp(-dt / 8), norm = 0;
        for (int d = 0; d < Dims; d++)
        {
            int a = Math.Max(1, (int)Math.Floor(Edges[d] / binHz));
            int z = Math.Min(fftSize / 2 - 1, Math.Max(a, (int)Math.Ceiling(Edges[d + 1] / binHz) - 1));
            double m = 0;
            for (int i = a; i <= z; i++) m += Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
            m /= z - a + 1;
            _bandPeak[d] = Math.Max(m, _bandPeak[d] * decay);
            // Rise per band relative to that band's own recent peak, so loud bands don't drown out the click.
            double f = _bandPeak[d] > 1e-9 ? Math.Max(0, m - _prevBand[d]) / _bandPeak[d] : 0;
            _prevBand[d] = m;
            _curFeature[d] = f;
            norm += f * f;
        }
        norm = Math.Sqrt(norm);
        if (norm > 1e-9)
            for (int d = 0; d < Dims; d++) _curFeature[d] /= norm;
    }

    void LearnTempo(double sinceLast)
    {
        if (_period > 0)
        {
            // Follow the tempo: every on-grid kick nudges the period, which tracks gradual speed-ups and slow-downs.
            double beats = Math.Round(sinceLast / _period);
            if (beats >= 1 && beats <= 4)
            {
                double perBeat = sinceLast / beats;
                if (Math.Abs(perBeat - _period) < 0.12 * _period)
                    _period = Math.Clamp(_period + 0.3 * (perBeat - _period), 0.25, 1.5);
            }
            return;
        }

        if (sinceLast is < 0.25 or > 1.5) return;

        // After a breakdown the tempo is usually unchanged: one interval that fits the old tempo relocks at once.
        if (_hintPeriod > 0)
        {
            double beats = Math.Round(sinceLast / _hintPeriod);
            if (beats is >= 1 and <= 2 && Math.Abs(sinceLast / beats - _hintPeriod) < 0.04 * _hintPeriod)
            {
                _period = sinceLast / beats;
                return;
            }
        }

        if (_recentKicks.Count < 6) return;
        var (best, fit) = InduceTempo();
        if (fit >= 0.8)
        {
            _period = best;
            _hintPeriod = 0;
            _strongOffGrid = 0;
        }
    }

    /// <summary>
    /// Finds the beat period that best explains the recent kicks: every pair of kicks should be a whole number of
    /// 16ths apart, ideally a whole number of beats. Works for four-on-the-floor and broken beats alike; a mild prior
    /// toward ~125 BPM settles half/double-tempo ambiguity. Returns the period and the share of consecutive
    /// intervals that sit on its 16th grid.
    /// </summary>
    (double Period, double Fit) InduceTempo()
    {
        var times = _recentKicks.ToArray();
        double bestScore = 0, best = 0;
        for (double p = 0.33; p <= 0.75; p += 0.002) // 80–180 BPM
        {
            double score = 0, sixteenth = p / 4;
            for (int i = 0; i < times.Length; i++)
            for (int j = i + 1; j < times.Length && times[j] - times[i] <= 3; j++)
            {
                double d = times[j] - times[i];
                score += Near(d, sixteenth, 0.012) + 2 * Near(d, p, 0.015);
            }
            double prior = Math.Log2(p / 0.48);
            score *= Math.Exp(-prior * prior / (2 * 0.35 * 0.35));
            if (score > bestScore) { bestScore = score; best = p; }
        }
        if (best == 0) return (0, 0);

        int onGrid = 0;
        for (int i = 1; i < times.Length; i++)
            if (Near(times[i] - times[i - 1], best / 4, 0.015) > 0.5) onGrid++;
        return (best, (double)onGrid / (times.Length - 1));
    }

    /// <summary>1 when <paramref name="d"/> is a whole multiple of <paramref name="unit"/>, falling off with the error in seconds.</summary>
    static double Near(double d, double unit, double tolerance)
    {
        double err = d - Math.Round(d / unit) * unit;
        return Math.Exp(-err * err / (2 * tolerance * tolerance));
    }

    double Percentile(double p)
    {
        int n = 0;
        foreach (var (v, _) in _peaks) _scratch[n++] = v;
        Array.Sort(_scratch, 0, n);
        return _scratch[(int)(p * (n - 1))];
    }

    static void Learn(double[] template, double[] feature, double rate)
    {
        double norm = 0;
        for (int d = 0; d < template.Length; d++)
        {
            template[d] += rate * (feature[d] - template[d]);
            norm += template[d] * template[d];
        }
        norm = Math.Sqrt(norm);
        if (norm > 1e-9)
            for (int d = 0; d < template.Length; d++) template[d] /= norm;
    }

    static double Cosine(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s; // both are unit length
    }

    static double FoldBpm(double bpm)
    {
        while (bpm < 90) bpm *= 2;
        while (bpm > 190) bpm /= 2;
        return Math.Round(bpm);
    }
}
