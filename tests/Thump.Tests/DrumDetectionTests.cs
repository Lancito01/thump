using Thump.Audio;

namespace Thump.Tests;

/// <summary>
/// Feeds synthetic dance music (kick, rolling 16th-note bassline, off-beat hats, optional claps or snares) through
/// the analyzer and scores the detected hits against where they really are. Supports tempo ramps, tempo jumps,
/// non-4/4 kick patterns and extra hits outside the usual pattern.
/// </summary>
public class DrumDetectionTests
{
    const int Sr = 48000;
    const double Warmup = 5; // seconds the detector gets to learn before we score

    internal record Track(float[] Audio, List<double> Kicks, List<double> Claps);
    internal record Score(double Recall, int FalsePositives, int Truth);
    internal record Result(Score Kick, Score Clap, AudioFeatures Final, List<double> KickTimes, int LockChangesAfterFirstLock);

    internal sealed class Spec
    {
        public double Seconds = 40;
        public double KickAmp = 0.8, BassAmp = 0.6, HatAmp = 0.15, ClapAmp = 0, Gain = 1, KickJitter = 0;
        public bool Snare;
        public Func<double, double> Bpm = _ => 128;
        public Func<int, bool> KickOnStep = step => step % 4 == 0; // step = 16th note within a 16-step bar
        public Func<int, int, bool> ExtraKick = (bar, step) => false;
        public Func<int, bool> ClapOnStep = step => step is 4 or 12;
        public Func<int, int, bool> ExtraClap = (bar, step) => false;
        public Func<double, bool> KicksOn = _ => true;
        public int Seed = 1;
    }

    internal static Track Render(Spec s)
    {
        var buf = new float[(int)(s.Seconds * Sr)];
        var rng = new Random(s.Seed);
        var kicks = new List<double>();
        var claps = new List<double>();
        double[] bassNotes = [55, 55, 49, 58.27];
        int step = 0;
        for (double t = 0; t < s.Seconds; t += 60 / s.Bpm(t) / 4, step++)
        {
            int inBar = step % 16;
            if ((s.KickOnStep(inBar) || s.ExtraKick(step / 16, inBar)) && s.KicksOn(t))
            {
                AddKick(buf, t, s.KickAmp * s.Gain * (1 + s.KickJitter * (rng.NextDouble() * 2 - 1)));
                kicks.Add(t);
            }
            else if (inBar % 4 != 0)
                AddBass(buf, t, bassNotes[(step / 4) % bassNotes.Length], s.BassAmp * s.Gain);
            if (inBar % 4 == 2)
                AddNoise(buf, t, 0.03, s.HatAmp * s.Gain, rng);
            if (s.ClapAmp > 0 && (s.ClapOnStep(inBar) || s.ExtraClap(step / 16, inBar)))
            {
                if (s.Snare) AddSnare(buf, t, s.ClapAmp * s.Gain, rng);
                else AddClap(buf, t, s.ClapAmp * s.Gain, rng);
                claps.Add(t);
            }
        }
        return new Track(buf, kicks, claps);
    }

    /// <summary>A produced-style kick: a short click, then a sine body sweeping from 120 Hz down to 48 Hz.</summary>
    static void AddKick(float[] buf, double t0, double amp)
    {
        int start = (int)(t0 * Sr);
        double phase = 0;
        for (int i = 0; i < (int)(0.3 * Sr) && start + i < buf.Length; i++)
        {
            double t = (double)i / Sr;
            double f = 48 + (120 - 48) * Math.Exp(-t / 0.02);
            phase += 2 * Math.PI * f / Sr;
            double click = t < 0.002 ? 0.06 * Math.Sin(2 * Math.PI * 3000 * t) : 0;
            buf[start + i] += (float)(amp * (Math.Exp(-t / 0.12) * Math.Sin(phase) + click));
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

    /// <summary>A hand clap: three quick noise bursts and a short tail, band-passed to roughly 0.8–4 kHz.</summary>
    static void AddClap(float[] buf, double t0, double amp, Random rng)
    {
        int start = (int)(t0 * Sr);
        double prev = 0, lp0 = 0, lp = 0, a = 1 - Math.Exp(-2 * Math.PI * 3500.0 / Sr);
        for (int i = 0; i < (int)(0.18 * Sr) && start + i < buf.Length; i++)
        {
            double t = (double)i / Sr, env = 0;
            for (int k = 0; k < 3; k++)
                if (t >= k * 0.011) env += Math.Exp(-(t - k * 0.011) / 0.004);
            if (t >= 0.03) env += 0.7 * Math.Exp(-(t - 0.03) / 0.05);
            double x = rng.NextDouble() * 2 - 1, hp = x - prev;
            prev = x;
            lp0 += a * (hp - lp0);
            lp += a * (lp0 - lp);
            buf[start + i] += (float)(amp * env * lp * 3);
        }
    }

    /// <summary>A snare: a 190 Hz body, a 330 Hz shell mode and a burst of noise rolling off above 5 kHz (the wires).</summary>
    static void AddSnare(float[] buf, double t0, double amp, Random rng)
    {
        int start = (int)(t0 * Sr);
        double prev = 0, lp0 = 0, lp = 0, a = 1 - Math.Exp(-2 * Math.PI * 5000.0 / Sr);
        for (int i = 0; i < (int)(0.2 * Sr) && start + i < buf.Length; i++)
        {
            double t = (double)i / Sr;
            double x = rng.NextDouble() * 2 - 1, hp = x - prev;
            prev = x;
            lp0 += a * (hp - lp0);
            lp += a * (lp0 - lp);
            double body = (0.6 * Math.Sin(2 * Math.PI * 190 * t) + 0.3 * Math.Sin(2 * Math.PI * 330 * t)) * Math.Exp(-t / 0.04);
            buf[start + i] += (float)(amp * (body + 2.4 * lp * Math.Exp(-t / 0.07)));
        }
    }

    internal static Result Run(Track track, double scoreFrom = Warmup, double sensitivity = 1, SongModel? remembered = null,
        AudioAnalyzer? analyzer = null)
    {
        var a = analyzer ?? new AudioAnalyzer { KickSensitivity = sensitivity, ClapSensitivity = sensitivity };
        if (remembered != null) a.StartTrack(remembered);
        var kicks = new List<double>();
        var claps = new List<double>();
        long lastKick = a.Snapshot().KickCount, lastClap = a.Snapshot().ClapCount;
        bool? locked = null;
        int lockChanges = 0;
        const int chunk = 480;
        for (int i = 0; i + chunk <= track.Audio.Length; i += chunk)
        {
            a.Feed(track.Audio.AsSpan(i, chunk), Sr);
            var f = a.Snapshot();
            double t = (double)(i + chunk) / Sr;
            if (f.KickCount != lastKick) { lastKick = f.KickCount; kicks.Add(t); }
            if (f.ClapCount != lastClap) { lastClap = f.ClapCount; claps.Add(t); }
            if (locked == null) { if (f.BeatLocked) locked = true; }
            else if (f.BeatLocked != locked) { locked = f.BeatLocked; lockChanges++; }
        }

        double end = (double)track.Audio.Length / Sr - 0.2;
        return new Result(Grade(track.Kicks, kicks, scoreFrom, end), Grade(track.Claps, claps, scoreFrom, end),
            a.Snapshot(), kicks, lockChanges);
    }

    /// <summary>A detection counts if it lands within 70 ms after a real hit (window + chunking latency).</summary>
    static Score Grade(List<double> truthAll, List<double> detections, double from, double end)
    {
        var truth = truthAll.Where(t => t >= from && t < end).ToList();
        var scored = detections.Where(d => d >= from).ToList();
        int hits = truth.Count(t => scored.Any(d => d >= t && d - t < 0.07));
        int fps = scored.Count(d => !truthAll.Any(t => d >= t && d - t < 0.07));
        return new Score(truth.Count == 0 ? 1 : (double)hits / truth.Count, fps, truth.Count);
    }

    static void AssertGood(Score s, double recall = 0.95, double fpRate = 0.04, string what = "kick")
    {
        Assert.True(s.Recall >= recall, $"{what} recall {s.Recall:P0}");
        Assert.True(s.FalsePositives <= Math.Max(1, s.Truth * fpRate), $"{s.FalsePositives} false {what}s / {s.Truth} real ones");
    }

    [Fact]
    public void Finds_kicks_and_ignores_rolling_bassline() => AssertGood(Run(Render(new Spec())).Kick);

    [Fact]
    public void Soft_kick_track() => AssertGood(Run(Render(new Spec { KickAmp = 0.35, BassAmp = 0.3 })).Kick, 0.9, 0.05);

    [Fact]
    public void Kick_loudness_varies_hit_to_hit() => AssertGood(Run(Render(new Spec { KickAmp = 0.7, BassAmp = 0.5, KickJitter = 0.3 })).Kick, 0.9, 0.05);

    [Fact]
    public void Quiet_master_behaves_like_loud_master()
    {
        var loud = Run(Render(new Spec { Seconds = 30 }));
        var quiet = Run(Render(new Spec { Seconds = 30, Gain = 0.03 }));
        AssertGood(quiet.Kick);
        Assert.True(Math.Abs(loud.Kick.FalsePositives - quiet.Kick.FalsePositives) <= 2, $"loud {loud.Kick.FalsePositives} vs quiet {quiet.Kick.FalsePositives} FPs");
    }

    [Fact]
    public void Locks_to_tempo()
    {
        var r = Run(Render(new Spec { Seconds = 20 }));
        Assert.True(r.Final.BeatLocked);
        Assert.InRange(r.Final.Bpm, 126, 130);
        Assert.True(r.Final.TempoConfidence > 0.8, $"tempo confidence {r.Final.TempoConfidence:P0}");
    }

    [Fact]
    public void Follows_gradual_tempo_ramp()
    {
        // 122 → 136 BPM over a minute, like a DJ riding the pitch fader.
        var r = Run(Render(new Spec { Seconds = 60, Bpm = t => 122 + 14 * t / 60 }));
        AssertGood(r.Kick, 0.93);
        Assert.True(r.Final.BeatLocked);
        Assert.InRange(r.Final.Bpm, 133, 137);
    }

    [Fact]
    public void Follows_tempo_slowing_down()
    {
        var r = Run(Render(new Spec { Seconds = 60, Bpm = t => 138 - 16 * t / 60 }));
        AssertGood(r.Kick, 0.93);
        Assert.InRange(r.Final.Bpm, 120, 124);
    }

    [Fact]
    public void Relocks_after_sudden_tempo_jump()
    {
        // Hard cut from 124 to 132 BPM at 30 s (a DJ transition). Allow 6 s to relock, then score.
        var r = Run(Render(new Spec { Seconds = 60, Bpm = t => t < 30 ? 124 : 132 }), scoreFrom: 36);
        AssertGood(r.Kick, 0.9, 0.05);
        Assert.True(r.Final.BeatLocked);
        Assert.InRange(r.Final.Bpm, 130, 134);
    }

    [Fact]
    public void Learns_broken_beat_pattern()
    {
        // Kicks on 16ths 0, 6 and 10 of the bar (two-step style), not on every beat.
        var r = Run(Render(new Spec { KickOnStep = s => s is 0 or 6 or 10, KickAmp = 0.8, BassAmp = 0.5 }), scoreFrom: 10);
        AssertGood(r.Kick, 0.85, 0.06);
    }

    [Fact]
    public void No_kick_follows_only_the_dominant_bass_hits()
    {
        // Bassline and hats only (3 bass notes per beat). With no kick the model learns the strongest low-end hits
        // as the pulse (fine for a bass-first light show), but it must not fire on every bass note.
        var r = Run(Render(new Spec { Seconds = 30, KickAmp = 0, KicksOn = _ => false }));
        int beats = (int)((30 - Warmup) / (60 / 128.0));
        Assert.True(r.Kick.FalsePositives <= beats * 2, $"{r.Kick.FalsePositives} detections over {beats} beats ({beats * 3} bass notes)");
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
        AssertGood(r.Kick, 0.9, 0.05);
    }

    // ---------- Sound over pattern ----------

    [Fact]
    public void Extra_kick_outside_the_pattern_still_counts()
    {
        // Half-time kicks on beats 1 and 3, and every fourth bar a triple kick (1, 2, 3) before the "drop". The
        // beat-2 kick doesn't fit the learned pattern, but it sounds exactly like the kick, so it must flash.
        double bar = 4 * 60 / 140.0;
        var track = Render(new Spec
        {
            Seconds = 50, Bpm = _ => 140,
            KickOnStep = s => s is 0 or 8,
            ExtraKick = (b, s) => b % 4 == 3 && s == 4,
        });
        var r = Run(track, scoreFrom: 8);
        AssertGood(r.Kick, 0.95, 0.04);
        var extras = track.Kicks.Where(t => t >= 8 && Math.Abs((t / bar) % 4 - 3.25) < 0.01).ToList();
        int found = extras.Count(t => r.KickTimes.Any(d => d >= t && d - t < 0.07));
        Assert.True(extras.Count >= 3 && found == extras.Count, $"{found} of {extras.Count} out-of-pattern kicks");
    }

    [Theory]
    [InlineData("4/4")]
    [InlineData("half-time")]
    [InlineData("broken")]
    public void Beat_status_does_not_flip_flop(string pattern)
    {
        // Regression: the grid used to anchor on a bass note, drift off, unlock and relock forever.
        Func<int, bool> kicks = pattern switch
        {
            "half-time" => s => s is 0 or 8,
            "broken" => s => s is 0 or 6 or 10,
            _ => s => s % 4 == 0,
        };
        var r = Run(Render(new Spec { Seconds = 60, KickOnStep = kicks }));
        Assert.True(r.Final.BeatLocked, "never locked");
        Assert.Equal(0, r.LockChangesAfterFirstLock);
    }

    // ---------- Claps and snares ----------

    [Fact]
    public void Finds_claps_on_two_and_four()
    {
        // Claps come half as often as kicks, so they get a little longer to learn.
        var r = Run(Render(new Spec { ClapAmp = 0.5 }), scoreFrom: 8);
        AssertGood(r.Clap, 0.95, 0.04, "clap");
        AssertGood(r.Kick);
    }

    [Fact]
    public void Finds_snares_with_half_time_kicks()
    {
        var r = Run(Render(new Spec { ClapAmp = 0.5, Snare = true, KickOnStep = s => s is 0 or 8 }), scoreFrom: 8);
        AssertGood(r.Clap, 0.95, 0.04, "snare");
        AssertGood(r.Kick);
    }

    [Fact]
    public void Clap_roll_before_the_drop_is_followed()
    {
        // Claps on 2 and 4, and every fourth bar a run of 8th-note claps in the second half. The off-beat ones hit
        // only about half as hard in the clap band as the usual claps (those land with a kick and its click), so a
        // few of them are missed; the rest of the roll must still flash.
        var r = Run(Render(new Spec { ClapAmp = 0.5, Seconds = 50, ExtraClap = (b, s) => b % 4 == 3 && s >= 8 && s % 2 == 0 }), scoreFrom: 8);
        AssertGood(r.Clap, 0.8, 0.05, "rolled clap");
    }

    [Fact]
    public void Hats_alone_are_not_claps()
    {
        // No claps at all: loud off-beat hats must not be taken for claps for long.
        var r = Run(Render(new Spec { HatAmp = 0.4 }), scoreFrom: 10);
        int beats = (int)((40 - 10) / (60 / 128.0));
        Assert.True(r.Clap.FalsePositives <= beats / 4, $"{r.Clap.FalsePositives} clap detections over {beats} beats of hats");
    }

    // ---------- Remembering songs ----------

    [Fact]
    public void Remembered_song_starts_out_knowing_its_drums()
    {
        var spec = new Spec { Seconds = 20, ClapAmp = 0.5 };
        using var first = new AudioAnalyzer();
        Run(Render(spec), analyzer: first);
        var model = first.ExportModel();
        Assert.True(model.Kick.Learned >= 16 && model.BeatPeriod > 0);

        // Second play: scored from the very first second.
        var fresh = Run(Render(new Spec { Seconds = 8, ClapAmp = 0.5 }), scoreFrom: 1);
        using var second = new AudioAnalyzer();
        var again = Run(Render(new Spec { Seconds = 8, ClapAmp = 0.5 }), scoreFrom: 1, remembered: model, analyzer: second);
        Assert.True(again.Final.Remembered);
        Assert.True(again.Kick.Recall >= 0.9, $"remembered kick recall {again.Kick.Recall:P0}");
        Assert.True(again.Kick.Recall > fresh.Kick.Recall, $"remembered {again.Kick.Recall:P0} vs fresh {fresh.Kick.Recall:P0}");
        Assert.True(again.Clap.Recall >= 0.85, $"remembered clap recall {again.Clap.Recall:P0}");
        Assert.True(again.Final.BeatLocked);
    }
}
