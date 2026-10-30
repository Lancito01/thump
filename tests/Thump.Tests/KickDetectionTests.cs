using Thump.Audio;

namespace Thump.Tests;

/// <summary>
/// Feeds synthetic techno (kick, rolling 16th-note bassline, off-beat hats) through the analyzer and scores the
/// detected kicks against where the kicks really are. Supports tempo ramps, tempo jumps and non-4/4 kick patterns.
/// </summary>
public class KickDetectionTests
{
    const int Sr = 48000;
    const double Warmup = 5; // seconds the detector gets to learn before we score

    record Track(float[] Audio, List<double> Kicks);
    record Result(double Recall, int FalsePositives, int TrueKicks, AudioFeatures Final, List<double> Detections);

    sealed class Spec
    {
        public double Seconds = 40;
        public double KickAmp = 0.8, BassAmp = 0.6, HatAmp = 0.15, Gain = 1, KickJitter = 0;
        public Func<double, double> Bpm = _ => 128;
        public Func<int, bool> KickOnStep = step => step % 4 == 0; // step = 16th note within a 16-step bar
        public Func<double, bool> KicksOn = _ => true;
        public int Seed = 1;
    }

    static Track Render(Spec s)
    {
        var buf = new float[(int)(s.Seconds * Sr)];
        var rng = new Random(s.Seed);
        var kicks = new List<double>();
        double[] bassNotes = [55, 55, 49, 58.27];
        int step = 0;
        for (double t = 0; t < s.Seconds; t += 60 / s.Bpm(t) / 4, step++)
        {
            int inBar = step % 16;
            if (s.KickOnStep(inBar) && s.KicksOn(t))
            {
                AddKick(buf, t, s.KickAmp * s.Gain * (1 + s.KickJitter * (rng.NextDouble() * 2 - 1)));
                kicks.Add(t);
            }
            else if (inBar % 4 != 0)
                AddBass(buf, t, bassNotes[(step / 4) % bassNotes.Length], s.BassAmp * s.Gain);
            if (inBar % 4 == 2)
                AddNoise(buf, t, 0.03, s.HatAmp * s.Gain, rng);
        }
        return new Track(buf, kicks);
    }

    static void AddKick(float[] buf, double t0, double amp)
    {
        int start = (int)(t0 * Sr);
        double phase = 0;
        for (int i = 0; i < (int)(0.3 * Sr) && start + i < buf.Length; i++)
        {
            double t = (double)i / Sr;
            double f = 48 + (150 - 48) * Math.Exp(-t / 0.025);
            phase += 2 * Math.PI * f / Sr;
            buf[start + i] += (float)(amp * Math.Exp(-t / 0.12) * Math.Sin(phase));
        }
    }

    static void AddBass(float[] buf, double t0, double freq, double amp)
    {
        int start = (int)(t0 * Sr);
        for (int i = 0; i < (int)(0.11 * Sr) && start + i < buf.Length; i++)
        {
            double t = (double)i / Sr;
            double env = Math.Min(1, t / 0.003) * Math.Exp(-t / 0.08);
            double v = 0;
            for (int h = 1; h <= 4; h++) v += Math.Sin(2 * Math.PI * freq * h * t) / h;
            buf[start + i] += (float)(amp * env * v * 0.6);
        }
    }

    static void AddNoise(float[] buf, double t0, double len, double amp, Random rng)
    {
        int start = (int)(t0 * Sr);
        double prev = 0;
        for (int i = 0; i < (int)(len * Sr) && start + i < buf.Length; i++)
        {
            double x = rng.NextDouble() * 2 - 1;
            double hp = x - prev; // crude high-pass so hats don't leak into the kick band
            prev = x;
            buf[start + i] += (float)(amp * hp * Math.Exp(-i / (0.008 * Sr)));
        }
    }

    static Result Run(Track track, double scoreFrom = Warmup, double sensitivity = 1)
    {
        using var a = new AudioAnalyzer { KickSensitivity = sensitivity };
        var detections = new List<double>();
        long last = 0;
        const int chunk = 480;
        for (int i = 0; i + chunk <= track.Audio.Length; i += chunk)
        {
            a.Feed(track.Audio.AsSpan(i, chunk), Sr);
            var f = a.Snapshot();
            if (f.KickCount != last)
            {
                last = f.KickCount;
                detections.Add((double)(i + chunk) / Sr);
            }
        }

        double end = (double)track.Audio.Length / Sr - 0.2;
        var truth = track.Kicks.Where(t => t >= scoreFrom && t < end).ToList();
        // A detection counts if it lands within 70 ms after a real kick (window + chunking latency).
        var scored = detections.Where(d => d >= scoreFrom).ToList();
        int hits = truth.Count(t => scored.Any(d => d >= t && d - t < 0.07));
        int fps = scored.Count(d => !track.Kicks.Any(t => d >= t && d - t < 0.07));
        return new Result(truth.Count == 0 ? 1 : (double)hits / truth.Count, fps, truth.Count, a.Snapshot(), detections);
    }

    static void AssertGood(Result r, double recall = 0.95, double fpRate = 0.04)
    {
        Assert.True(r.Recall >= recall, $"recall {r.Recall:P0}");
        Assert.True(r.FalsePositives <= Math.Max(1, r.TrueKicks * fpRate), $"{r.FalsePositives} false positives / {r.TrueKicks} kicks");
    }

    [Fact]
    public void Finds_kicks_and_ignores_rolling_bassline() => AssertGood(Run(Render(new Spec())));

    [Fact]
    public void Soft_kick_track() => AssertGood(Run(Render(new Spec { KickAmp = 0.35, BassAmp = 0.3 })), 0.9, 0.05);

    [Fact]
    public void Kick_loudness_varies_hit_to_hit() => AssertGood(Run(Render(new Spec { KickAmp = 0.7, BassAmp = 0.5, KickJitter = 0.3 })), 0.9, 0.05);

    [Fact]
    public void Quiet_master_behaves_like_loud_master()
    {
        var loud = Run(Render(new Spec { Seconds = 30 }));
        var quiet = Run(Render(new Spec { Seconds = 30, Gain = 0.03 }));
        AssertGood(quiet);
        Assert.True(Math.Abs(loud.FalsePositives - quiet.FalsePositives) <= 2, $"loud {loud.FalsePositives} vs quiet {quiet.FalsePositives} FPs");
    }

    [Fact]
    public void Locks_to_tempo()
    {
        var r = Run(Render(new Spec { Seconds = 20 }));
        Assert.True(r.Final.BeatLocked);
        Assert.InRange(r.Final.Bpm, 126, 130);
    }

    [Fact]
    public void Follows_gradual_tempo_ramp()
    {
        // 122 → 136 BPM over a minute, like a DJ riding the pitch fader.
        var r = Run(Render(new Spec { Seconds = 60, Bpm = t => 122 + 14 * t / 60 }));
        AssertGood(r, 0.93);
        Assert.True(r.Final.BeatLocked);
        Assert.InRange(r.Final.Bpm, 133, 137);
    }

    [Fact]
    public void Follows_tempo_slowing_down()
    {
        var r = Run(Render(new Spec { Seconds = 60, Bpm = t => 138 - 16 * t / 60 }));
        AssertGood(r, 0.93);
        Assert.InRange(r.Final.Bpm, 120, 124);
    }

    [Fact]
    public void Relocks_after_sudden_tempo_jump()
    {
        // Hard cut from 124 to 132 BPM at 30 s (a DJ transition). Allow 6 s to relock, then score.
        var r = Run(Render(new Spec { Seconds = 60, Bpm = t => t < 30 ? 124 : 132 }), scoreFrom: 36);
        AssertGood(r, 0.9, 0.05);
        Assert.InRange(r.Final.Bpm, 130, 134);
    }

    [Fact]
    public void Learns_broken_beat_pattern()
    {
        // Kicks on 16ths 0, 6 and 10 of the bar (two-step style), not on every beat.
        var r = Run(Render(new Spec { KickOnStep = s => s is 0 or 6 or 10, KickAmp = 0.8, BassAmp = 0.5 }), scoreFrom: 10);
        AssertGood(r, 0.85, 0.06);
    }

    [Fact]
    public void No_kick_follows_only_the_dominant_bass_hits()
    {
        // Bassline and hats only (3 bass notes per beat). With no kick the model learns the strongest low-end hits
        // as the pulse (fine for a bass-first light show), but it must not fire on every bass note.
        var r = Run(Render(new Spec { Seconds = 30, KickAmp = 0, KicksOn = _ => false }));
        int beats = (int)((30 - Warmup) / (60 / 128.0));
        Assert.True(r.FalsePositives <= beats * 2, $"{r.FalsePositives} detections over {beats} beats ({beats * 3} bass notes)");
    }

    [Fact]
    public void Recovers_after_breakdown_with_quieter_kick()
    {
        // 32 beats of hard kicks, an 18-beat breakdown (bass only), then the drop returns with a softer kick.
        double beat = 60 / 128.0;
        var track = Render(new Spec
        {
            Seconds = 45,
            KicksOn = t => t < 32 * beat || t >= 50 * beat,
        });
        // Make the post-breakdown kicks softer by re-rendering just that section at a lower kick level.
        var soft = Render(new Spec { Seconds = 45, KickAmp = 0.5, KicksOn = t => t >= 50 * beat });
        int cut = (int)(50 * beat * Sr);
        Array.Copy(soft.Audio, cut, track.Audio, cut, track.Audio.Length - cut);
        var r = Run(track, scoreFrom: 50 * beat + 3);
        AssertGood(r, 0.9, 0.05);
    }
}
