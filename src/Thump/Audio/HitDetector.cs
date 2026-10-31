using NAudio.Dsp;

namespace Thump.Audio;

/// <summary>
/// A small per-song learner that decides, hit by hit, whether a sound was this track's kick (or clap/snare). Every
/// onset in its band gets a <b>confidence</b>: how sure the model is that this sound was the drum. When it clears the
/// threshold, the lights flash. What it learns about the track, starting from scratch on every new song (or from what
/// it remembered last time):
/// <list type="bullet">
/// <item><b>Sound</b>: a fingerprint of the drum's attack across 14 bands, plus one of the hits that turned out not to
/// be it (bass notes, hats, vocals). Each candidate is compared with both: a tiny nearest-template classifier.</item>
/// <item><b>Strength</b>: how hard this track's drum hits, and how far it stands out from the other onsets in its
/// band, so quiet and loud ones both work and a bassline with no kick under it isn't taken for one.</item>
/// <item><b>Timing</b>: whether the sound landed on the beat grid, and how often the drum falls on that 16th of the
/// beat, weighted by how sure the tempo is. This only nudges borderline sounds (a bass note right after the kick).
/// A sound that is clearly the drum counts wherever it lands, so fills, rolls and the extra kick before a drop show
/// up.</item>
/// </list>
/// It teaches itself only from hits it is sure about (self-training), so a doubtful sound can't drag the model off.
/// Claps and snares are also checked against how they ring out a moment later (a clap rings for 100–200 ms, a hi-hat
/// or a click is gone in 30): that check labels the examples it learns from, but never delays a flash.
/// Everything is O(bins) per FFT hop; no offline training, no model files.
/// </summary>
public sealed class HitDetector
{
    /// <summary>1 = default; higher accepts sounds the model is less sure about.</summary>
    public double Sensitivity { get; set; } = 1;

    readonly double _lowHz, _highHz, _refractory;
    readonly int _firstBand, _dims;
    // What this kind of drum sounds like before anything is learned, as log-odds evidence (0 = no opinion), and how
    // much of that a sound needs to become one of the first examples.
    readonly Func<SpectralFeatures, double, double>? _soundPrior; // (spectrum, onset flatness) → evidence
    readonly double _priorWeight;  // how much the generic prior still counts once the song's own model has formed
    readonly double _labelPrior;   // a ring-out alone isn't enough to learn from a sound with less prior than this
    readonly double _bootstrapPrior;
    // How the drum dies away: checked a few hops after each onset to label what the model learns from.
    readonly RingOut _ring;

    /// <summary>0..1: how much the model has learned about this track's drum.</summary>
    public double Learned => Math.Min(1, _learned / 16.0);
    /// <summary>Confidence (0..1) of the latest accepted hit.</summary>
    public double LastConfidence { get; private set; }

    // Diagnostics for tests: (time s, strength / typical, timbre similarity, timing evidence, confidence, accepted)
    internal Action<double, double, double, double, double, bool>? Trace;

    // Onset strength (own band, linear magnitude rise normalised by the band's recent peak)
    double[] _prevMag = [];
    double _bandPeak;
    double _prev1Strength, _prev2Strength;
    // Spectral flatness of the band in the latest two hops (the onset is picked one hop after its peak).
    double[] _mag = [];
    double _flatness, _prevFlatness;

    /// <summary>Geometric over arithmetic mean: near 1 for noise spread evenly, lower for a few strong peaks.</summary>
    static double Flatness(double[] v, double mean)
    {
        if (mean <= 1e-12) return 0;
        double floor = 0.01 * mean, logs = 0;
        foreach (var x in v) logs += Math.Log(x + floor);
        return Math.Exp(logs / v.Length) / (mean + floor);
    }

    readonly double[] _energy = new double[16]; // the band's total magnitude over the last hops, for the ring-out check
    long _hop;

    // Sound model
    readonly double[] _feature;
    readonly double[] _hitTemplate;
    readonly double[] _otherTemplate;
    int _otherCount;
    double _typical;
    int _learned;

    // How often the drum falls on each 16th of the beat, relative to the beat tracker's grid
    readonly double[] _slotProb = [0.5, 0.5, 0.5, 0.5];
    int _gridVersion = -1;

    readonly Queue<(double Value, long Sample)> _peaks = new();
    readonly double[] _scratch = new double[1024];
    readonly Queue<Pending> _pending = new();
    long _lastHitSample = long.MinValue / 2;
    int _unexplained; // loud, drum-like sounds rejected since the last hit
    double _shapeRate; // share of would-be hits lately that died away like the drum

    /// <summary>A candidate waiting for its ring-out check before the model learns from it.</summary>
    sealed record Pending(long Hop, double[] Feature, double Strength, double Rel, double Confidence, bool Accepted, double Prior);

    /// <summary>
    /// How a drum dies away, measured as the band's level some hops after the onset, relative to the onset's rise
    /// (1 = still as loud, 0 = back to where it was). <c>Lowest</c>: judge by the lowest level reached from 40 ms on
    /// (a later note in the same band doesn't spoil it) instead of the level at the last hop.
    /// </summary>
    sealed record RingOut(int Hops, bool Lowest, double DrumMin, double DrumMax, double OtherBelow, double OtherAbove);

    HitDetector(double lowHz, double highHz, double fromHz, double toHz, double refractory, RingOut ring,
        Func<SpectralFeatures, double, double>? soundPrior = null, double bootstrapPrior = 0, double priorWeight = 0.4,
        double labelPrior = double.NegativeInfinity)
    {
        _priorWeight = priorWeight;
        _labelPrior = labelPrior;
        _bootstrapPrior = bootstrapPrior;
        _lowHz = lowHz;
        _highHz = highHz;
        _refractory = refractory;
        _soundPrior = soundPrior;
        _ring = ring;
        _firstBand = SpectralFeatures.BandAt(fromHz);
        _dims = (toHz >= 16000 ? SpectralFeatures.Dims : SpectralFeatures.BandAt(toHz)) - _firstBand;
        _feature = new double[_dims];
        _hitTemplate = new double[_dims];
        _otherTemplate = new double[_dims];
    }

    /// <summary>
    /// Kick drum: onsets in 40–130 Hz, fingerprinted from 30 Hz to 2.5 kHz (above that a kick's click and a snare's
    /// rattle look alike). A kick thumps at 45–140 Hz, punches out
    /// of the mix and is gone within about 130 ms; a bass or 808 note, a tom, a swell or a pad keeps going, sits higher
    /// or lower, or hardly stands out.
    /// </summary>
    public static HitDetector Kick() =>
        new(40, 130, 0, 2500, 0.09, new RingOut(12, Lowest: true, DrumMin: double.NegativeInfinity, DrumMax: 0.45,
            OtherBelow: double.NegativeInfinity, OtherAbove: 0.75), (s, _) => s.KickShape(), bootstrapPrior: 0.3);

    /// <summary>
    /// Claps and snares: onsets in 1–6 kHz, fingerprinted from 190 Hz up (kick and bass don't matter). A clap or a
    /// snare's wires are noise: at the hit the 1–6 kHz band is flat (spectral flatness ≳ 0.75), where a synth, piano or
    /// vocal note is a comb of harmonics and a hi-hat leaves the low end of the band empty (≲ 0.7). It also rattles mostly in the mids (hi-hats sit above 5 kHz) and rings
    /// out for 100–200 ms (a hat or a click is gone in 30). Those cues stay in force all along, not just at the start.
    /// </summary>
    public static HitDetector Clap() =>
        new(1000, 6000, 190, 16000, 0.07, new RingOut(6, Lowest: false, DrumMin: 0.15, DrumMax: 0.85, OtherBelow: 0.1, OtherAbove: 1),
            (s, flatness) => Math.Clamp(2.5 * (s.MidShare() - 0.3), -1, 0.5) + Math.Clamp(10 * (flatness - 0.75), -2, 0.4),
            bootstrapPrior: 0.7, priorWeight: 1, labelPrior: 0);

    public void Reset()
    {
        _peaks.Clear();
        _pending.Clear();
        _typical = 0;
        _learned = 0;
        Array.Clear(_hitTemplate);
        Array.Clear(_otherTemplate);
        _otherCount = 0;
        Array.Fill(_slotProb, 0.5);
        _unexplained = 0;
        _shapeRate = 0;
        LastConfidence = 0;
    }

    /// <summary>Feed one FFT frame. Returns the confidence of the hit in the previous hop, or null if there wasn't one.</summary>
    public double? Process(Complex[] fft, int fftSize, double binHz, double dt, SpectralFeatures spectrum, BeatTracker beat,
        long hopEndSample, int hopSize, int sampleRate)
    {
        double strength = BandOnset(fft, fftSize, binHz, dt);
        long hop = _hop++;
        while (_pending.Count > 0 && hop >= _pending.Peek().Hop + _ring.Hops) Confirm(_pending.Dequeue(), hop);

        // Peak picking: was the previous hop a local maximum? (one hop ≈ 11 ms of latency)
        double cand = _prev1Strength;
        bool isPeak = cand > _prev2Strength && cand >= strength;
        long candSample = hopEndSample - hopSize;
        double time = (double)candSample / sampleRate;
        _prev2Strength = _prev1Strength;
        _prev1Strength = strength;

        double sinceLast = (double)(candSample - _lastHitSample) / sampleRate;
        // Breakdown: slowly forget the drum's strength so a softer one after the drop is found again, but keep the
        // sound model so the bassline or pads that carry the breakdown aren't mistaken for it.
        if (sinceLast > 4) _typical *= Math.Exp(-dt / 6);

        if (beat.GridVersion != _gridVersion)
        {
            // New grid, new phase: a pattern learned on the old one would point at the wrong 16ths.
            _gridVersion = beat.GridVersion;
            Array.Fill(_slotProb, 0.5);
        }

        if (!isPeak || cand < MinOnset) return null;
        _peaks.Enqueue((cand, candSample));
        while (_peaks.Count > 0 && candSample - _peaks.Peek().Sample > 4L * sampleRate) _peaks.Dequeue();
        if (_peaks.Count < 8) return null;

        Fingerprint(spectrum.Previous);
        double sens = Math.Max(0.2, Sensitivity);
        double prior = _soundPrior?.Invoke(spectrum, _prevFlatness) ?? 0;
        double rel, sim = 0, grid = 0, confidence;
        bool accept;

        if (_learned < Bootstrap)
        {
            // Nothing learned yet: only the clearest hits count (close to the loudest of the last few seconds, and
            // not obviously the wrong kind of sound), and they become the first examples.
            rel = cand / Math.Max(1e-6, PeakMax());
            bool clear = candSample - _peaks.Peek().Sample > 2L * sampleRate && rel >= 0.6 / sens
                         && (_soundPrior == null || prior > _bootstrapPrior) && sinceLast >= 0.25;
            confidence = clear ? 0.8 : 0;
            // Stays dark until a couple of examples have died away like the drum, so the loudest notes of an intro
            // with no drum in it are never taken for one.
            accept = clear && _learned >= 2 && _shapeRate >= ShapeGate;
            if (clear)
            {
                _pending.Enqueue(new Pending(hop - 1, (double[])_feature.Clone(), cand, 1, confidence, accept, prior));
                _lastHitSample = candSample;
            }
        }
        else
        {
            rel = cand / Math.Max(1e-6, _typical);
            sim = Cosine(_feature, _hitTemplate) - (_otherCount > 4 ? Cosine(_feature, _otherTemplate) : 0.5);
            // Between 16ths is a little evidence against; on a 16th where this drum usually is, a little for; on one
            // where it rarely is, a little against. All as far as the tempo can be trusted.
            if (beat.TryGetSlot(time, out int slot, out bool onGrid))
                grid =beat.Confidence * (onGrid ? Math.Clamp(0.5 * Math.Log(0.4 + 1.2 * _slotProb[slot]), -0.15, 0.2) : -0.3);

            // Hard on the heels of the last hit (well under half a beat) it takes a little more to be believed: a roll
            // is clearly the drum every time, a bass note right after the kick is only half-convincing.
            double crowded = sinceLast < 0.4 * (beat.Period > 0 ? beat.Period : 0.5) ? -0.25 : 0;

            // A drum stands out from everything else in its band: kicks hit 2–3× harder than the bass notes around
            // them. A run of equally loud look-alikes (a bassline with no kick under it) doesn't.
            double standOut = Math.Clamp(Math.Log(cand / Math.Max(1e-6, PeakUpperQuartile())) - Math.Log(1.5), -0.4, 0.3);

            // Evidence in log-odds: as loud as this track's drum, sounds like what it learned (the main thing), stands
            // out, and (a little) on the beat. The timing term can move the bar by only about ±30%, so it tips
            // doubtful sounds but never vetoes a clear one. The generic sound prior steps back now that the model
            // knows this track's drum (except for claps, where it keeps ruling out sounds that plainly aren't noise).
            double margin = Math.Log(Math.Max(rel, 1e-3)) + 1.5 * Math.Log(Math.Max(1 + sim, 1e-3)) + standOut
                            + grid + crowded + _priorWeight * prior + Math.Log(sens / 0.8);
            confidence = 1 / (1 + Math.Exp(-4 * margin));
            // Also dark while what sounds like the drum mostly hasn't been dying away like one (a section where the
            // "kick" is really a bass note): the model has to keep earning its trust.
            accept = confidence >= 0.5 && sinceLast >= _refractory && _shapeRate >= ShapeGate;

            if (onGrid && rel > 0.4)
                _slotProb[slot] = Math.Clamp(_slotProb[slot] + 0.06 * ((accept ? 1 : 0) - _slotProb[slot]), 0.1, 0.95);

            // Not the echo of the hit just before (a clap is three or four bursts in quick succession).
            if (rel > 0.3 && sinceLast >= _refractory)
                _pending.Enqueue(new Pending(hop - 1, (double[])_feature.Clone(), cand, rel, confidence, accept, prior));

            if (!accept && rel >= 1 && sim > 0)
            {
                // Loud and drum-like, but the model keeps saying no: after a long run of these with no hits at all,
                // the drum has probably changed (new track without media info, or a very different section).
                if (++_unexplained > 24 && sinceLast > 10)
                {
                    _learned = 0;
                    _unexplained = 0;
                }
            }
        }

        Trace?.Invoke(time, rel, sim, grid, confidence, accept);
        if (!accept) return null;

        _unexplained = 0;
        _lastHitSample = candSample;
        LastConfidence = confidence;
        return confidence;
    }

    const int Bootstrap = 6;
    const double ShapeGate = 0.4;
    const double MinOnset = 4; // % of the band's recent peak

    /// <summary>
    /// The ring-out check, a few hops after the onset: a kick is a thump that's gone again, a clap or snare a sharp
    /// rise that then rings, a hi-hat or a click a rise that's already gone, a bass note, pad or vocal a rise that just
    /// stays. The answer labels what the model learns from: a confirmed hit (even one the model missed) teaches it
    /// what the drum sounds like, and a clear non-drum what to ignore. How often would-be hits pass also decides
    /// whether the lights may flash at all.
    /// </summary>
    void Confirm(Pending p, long now)
    {
        double before = Energy(p.Hop - 1), onset = Math.Max(Energy(p.Hop), Energy(p.Hop + 1));
        double rise = onset - before;
        if (rise <= 0.5 * before) return; // buried in something already loud: no telling, so no lesson either
        double tail = Energy(now);
        if (_ring.Lowest)
            for (long h = p.Hop + 4; h < now; h++) tail = Math.Min(tail, Energy(h));
        double ring = (tail - before) / rise;

        bool drum = ring >= _ring.DrumMin && ring <= _ring.DrumMax && p.Prior > _labelPrior;
        bool other = ring < _ring.OtherBelow || ring > _ring.OtherAbove;
        if (drum && _learned is > 0 and < 2 * Bootstrap && p.Strength > 2.5 * _typical)
        {
            // Barely learned, and here's a drum-shaped hit far louder than anything so far: the real drum just came
            // in (the drop after an intro of softer look-alikes). Start the model over from this one.
            _learned = 0;
            Array.Clear(_hitTemplate);
        }
        if (p.Confidence >= 0.5 && (drum || other)) _shapeRate += 0.15 * ((drum ? 1 : 0) - _shapeRate);
        if (drum && p.Accepted) LearnHit(p.Feature, p.Strength, p.Confidence);
        // A missed one teaches it too, if it was nearly as loud as the drum and sounded the part: that's how a quiet
        // drum the model was too strict about gets learned. (A rolling bassline also dies away quickly, but it's
        // softer and doesn't sound like a drum.)
        else if (drum && p.Rel >= 0.8 && (_soundPrior == null || p.Prior > _bootstrapPrior)) LearnHit(p.Feature, p.Strength, 0.65);
        else if (other) LearnOther(p.Feature);
    }

    double Energy(long hop) => _energy[(int)(hop % _energy.Length)];

    /// <summary>Self-training: only hits it's sure about teach the model, and the surer, the more.</summary>
    void LearnHit(double[] feature, double strength, double confidence)
    {
        double rate = Math.Clamp((confidence - 0.55) / 0.25, 0, 1);
        if (rate <= 0) return;
        if (_learned < Bootstrap)
        {
            // The first examples are averaged, so no single one (the first, or an odd one) dominates the model.
            double w = 1.0 / (_learned + 1);
            Learn(_hitTemplate, feature, w);
            _typical = _learned == 0 ? strength : _typical + w * (strength - _typical);
        }
        else
        {
            Learn(_hitTemplate, feature, 0.15 * rate);
            // Strength follows the louder hits more readily than the softer ones, so the drum stays the dominant hit
            // and a run of quieter look-alikes can't talk the bar down.
            double target = Math.Min(strength, 1.5 * _typical);
            _typical += (target > _typical ? 0.15 : 0.05) * rate * (target - _typical);
        }
        if (confidence >= 0.65) _learned++;
    }

    void LearnOther(double[] feature) => Learn(_otherTemplate, feature, _otherCount++ == 0 ? 1 : 0.1);

    double BandOnset(Complex[] fft, int fftSize, double binHz, double dt)
    {
        int a = Math.Max(1, (int)Math.Floor(_lowHz / binHz));
        int z = Math.Min(fftSize / 2 - 1, (int)Math.Ceiling(_highHz / binHz));
        int n = z - a + 1;
        bool first = _prevMag.Length != n;
        if (first) _prevMag = new double[n];

        // Linear magnitude rise, so within a track a loud hit outranks a quieter note (a rise in dB would rank a
        // quiet note out of silence as high as a loud kick). Normalised by the band's recent peak, so it doesn't
        // depend on how loud the track is mastered.
        double rise = 0, total = 0;
        if (_mag.Length != n) _mag = new double[n];
        for (int i = 0; i < n; i++)
        {
            var c = fft[a + i];
            double m = Math.Sqrt(c.X * c.X + c.Y * c.Y);
            if (!first) rise += Math.Max(0, m - _prevMag[i]);
            total += m;
            _prevMag[i] = m;
            _mag[i] = m;
        }
        _prevFlatness = _flatness;
        _flatness = Flatness(_mag, total / n);
        _energy[(int)(_hop % _energy.Length)] = total;
        _bandPeak = Math.Max(total, _bandPeak * Math.Exp(-dt / 8));
        return _bandPeak > 1e-7 ? 100 * rise / _bandPeak : 0; // ≤ 1e-7 ≈ digital silence
    }

    void Fingerprint(double[] rise)
    {
        double norm = 0;
        for (int d = 0; d < _dims; d++)
        {
            _feature[d] = rise[_firstBand + d];
            norm += _feature[d] * _feature[d];
        }
        norm = Math.Sqrt(norm);
        if (norm > 1e-9)
            for (int d = 0; d < _dims; d++) _feature[d] /= norm;
    }

    /// <summary>A typical clear onset in this band lately (the many tiny wiggles in between don't count).</summary>
    double PeakUpperQuartile()
    {
        int n = 0;
        foreach (var (v, _) in _peaks) _scratch[n++] = v;
        Array.Sort(_scratch, 0, n);
        return _scratch[3 * (n - 1) / 4];
    }

    double PeakMax()
    {
        double max = 0;
        foreach (var (v, _) in _peaks) max = Math.Max(max, v);
        return max;
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

    // ---------- Remembering songs ----------

    /// <summary>What's worth keeping between plays: the sound model and strength, not the rhythm (that's relative
    /// to a beat phase that won't be the same next time).</summary>
    public HitModel Export() => new()
    {
        Hit = (double[])_hitTemplate.Clone(),
        Other = (double[])_otherTemplate.Clone(),
        OtherCount = _otherCount,
        Typical = _typical,
        Learned = _learned,
    };

    public void Import(HitModel m)
    {
        Reset();
        if (m.Hit.Length != _dims || m.Other.Length != _dims) return; // from an older layout: learn afresh
        m.Hit.CopyTo(_hitTemplate, 0);
        m.Other.CopyTo(_otherTemplate, 0);
        _otherCount = m.OtherCount;
        _typical = m.Typical;
        _learned = m.Learned;
        if (_learned >= Bootstrap) _shapeRate = 0.6; // it earned that trust last time; the checks carry on from here
    }
}
