using System.Collections.Concurrent;
using System.Globalization;
using Thump.Audio;
using ThumpTool;

// Offline tools for the drum model. See tools/README.md.
//   analyze <music dir> <out dir>   features (.thf) + the current detectors' hits (.hits.csv) per track
//   score <labels dir> <hits dir>   how well the hits match the labels, per drum and per genre (top folder)

if (args.Length == 3 && args[0] == "analyze") return Analyze(args[1], args[2]);
if (args.Length == 3 && args[0] == "score") return ScoreAll(args[1], args[2]);
Console.Error.WriteLine("usage: ThumpTool analyze <music dir> <out dir> | score <labels dir> <hits dir>");
return 1;

static int Analyze(string musicDir, string outDir)
{
    string[] exts = [".m4a", ".mp3", ".wav", ".flac", ".aac", ".opus"];
    var files = Directory.EnumerateFiles(musicDir, "*", SearchOption.AllDirectories)
        .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
    int done = 0, failed = 0;
    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, file =>
    {
        var stem = Path.Combine(outDir, Path.ChangeExtension(Path.GetRelativePath(musicDir, file), null));
        if (!File.Exists(stem + ".hits.csv"))
            try { AnalyzeTrack(file, stem); }
            catch (Exception e) { Interlocked.Increment(ref failed); Console.Error.WriteLine($"{file}: {e.Message}"); }
        int n = Interlocked.Increment(ref done);
        if (n % 25 == 0 || n == files.Count) Console.WriteLine($"{n}/{files.Count}");
    });
    Console.WriteLine($"done, {failed} failed");
    return 0;
}

// Decoded by ffmpeg, like the teacher: Windows' AAC decoder keeps the encoder's ~48 ms of priming silence, which
// would put every feature that much later than its label. Mono at 48 kHz, what Windows usually hands Thump, so every
// hop is the same length as live.
static void AnalyzeTrack(string file, string stem)
{
    const int Rate = 48000;
    Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
    var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var arg in new[] { "-v", "error", "-i", file, "-ac", "1", "-ar", Rate.ToString(), "-f", "f32le", "-" })
        psi.ArgumentList.Add(arg);
    using var ffmpeg = System.Diagnostics.Process.Start(psi)!;
    ffmpeg.ErrorDataReceived += (_, _) => { };
    ffmpeg.BeginErrorReadLine();
    var pcm = ffmpeg.StandardOutput.BaseStream;

    var hits = new List<Hit>();
    int frames = 0;
    using var thf = new BinaryWriter(File.Create(stem + ".thf.tmp"));
    // Header: magic, bands, sample rate, hop (samples), frames; then frames × bands float16, one row per hop.
    thf.Write("THF1"u8);
    thf.Write(ModelFeatures.Bands);
    thf.Write(Rate);
    thf.Write(AudioAnalyzer.Hop);
    thf.Write(0);
    using (var a = new AudioAnalyzer())
    {
        a.HopAnalyzed = (t, values, kick, clap) =>
        {
            foreach (var v in values) thf.Write((Half)v);
            frames++;
            if (kick is { } k) hits.Add(new Hit(t, "kick", k));
            if (clap is { } c) hits.Add(new Hit(t, "snare", c));
        };
        var bytes = new byte[4 * 4800];
        int have = 0, read;
        while ((read = pcm.Read(bytes, have, bytes.Length - have)) > 0)
        {
            have += read;
            int whole = have / 4 * 4;
            a.Feed(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole)), Rate);
            Array.Copy(bytes, whole, bytes, 0, have - whole);
            have -= whole;
        }
    }
    ffmpeg.WaitForExit();
    if (ffmpeg.ExitCode != 0 || frames == 0) throw new InvalidOperationException($"ffmpeg failed ({ffmpeg.ExitCode})");
    thf.Seek(16, SeekOrigin.Begin);
    thf.Write(frames);
    thf.Dispose();
    File.Move(stem + ".thf.tmp", stem + ".thf", true);
    Scoring.Write(stem + ".hits.csv", hits); // written last: its presence marks the track as done
}

static int ScoreAll(string labelsDir, string hitsDir)
{
    string[] drums = ["kick", "snare", "hihat"];
    var byGenre = new ConcurrentDictionary<string, Dictionary<string, DrumScore>>();
    int tracks = 0;
    foreach (var labels in Directory.EnumerateFiles(labelsDir, "*.csv", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(labelsDir, labels);
        var hits = Path.Combine(hitsDir, Path.ChangeExtension(rel, null) + ".hits.csv");
        if (!File.Exists(hits)) continue;
        tracks++;
        var l = Scoring.Read(labels);
        var d = Scoring.Read(hits);
        var genre = rel.Contains(Path.DirectorySeparatorChar) ? rel[..rel.IndexOf(Path.DirectorySeparatorChar)] : "-";
        foreach (var g in new[] { genre, "ALL" })
        {
            var s = byGenre.GetOrAdd(g, _ => drums.ToDictionary(x => x, _ => new DrumScore()));
            foreach (var drum in drums) s[drum].Add(Scoring.Score(l, d, drum));
        }
    }
    Console.WriteLine($"{tracks} tracks. Per drum: F1 (precision / recall), median latency");
    foreach (var (genre, s) in byGenre.OrderBy(kv => kv.Key == "ALL").ThenBy(kv => kv.Key))
        Console.WriteLine($"{genre,-12} " + string.Join("   ", drums.Where(x => s[x].Labels + s[x].Detections > 0).Select(x =>
            string.Create(CultureInfo.InvariantCulture,
                $"{x} {s[x].F1:0.00} ({s[x].Precision:0.00}/{s[x].Recall:0.00}) {s[x].MedianLatency * 1000:0}ms"))));
    return 0;
}
