using System.IO;
using NAudio.Wave;
using Thump.Audio;
using Xunit.Abstractions;

namespace Thump.Tests;

/// <summary>
/// Runs the analyzer over a real recording and prints what it heard. Set THUMP_WAV to a WAV file to enable
/// (e.g. a loopback recording of a track); skipped otherwise. Useful for tuning, not a pass/fail check.
/// </summary>
public class RealMusicBench(ITestOutputHelper output)
{
    [Fact]
    public void Bench()
    {
        var path = Environment.GetEnvironmentVariable("THUMP_WAV");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("THUMP_WAV not set; skipping.");
            return;
        }

        using var reader = new AudioFileReader(path);
        int ch = reader.WaveFormat.Channels, sr = reader.WaveFormat.SampleRate;
        var buf = new float[ch * 480];
        var mono = new float[480];
        using var a = new AudioAnalyzer();
        long last = 0, lastClap = 0, frames = 0;
        var kicks = new List<double>();
        var claps = new List<double>();
        double nextReport = 5;
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            int n = read / ch;
            for (int i = 0; i < n; i++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += buf[i * ch + c];
                mono[i] = sum / ch;
            }
            a.Feed(mono.AsSpan(0, n), sr);
            frames += n;
            double t = (double)frames / sr;
            var f = a.Snapshot();
            if (f.KickCount != last) { last = f.KickCount; kicks.Add(t); }
            if (f.ClapCount != lastClap) { lastClap = f.ClapCount; claps.Add(t); }
            if (t >= nextReport)
            {
                var recent = kicks.Where(k => k > t - 5).ToList();
                var gaps = recent.Zip(recent.Skip(1), (x, y) => y - x).ToList();
                output.WriteLine($"{t,5:0}s  kicks/5s={recent.Count,3}  claps/5s={claps.Count(c => c > t - 5),3}  bpm={f.Bpm,4:0}  locked={f.BeatLocked,-5}  learned={f.KickLearned:0.00} clap={f.ClapLearned:0.00} tempo={f.TempoConfidence:0.00}  " +
                                 $"gaps={string.Join(" ", gaps.Take(12).Select(g => g.ToString("0.00")))}");
                nextReport += 5;
            }
        }
    }
}
