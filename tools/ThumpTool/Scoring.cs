using System.Globalization;

namespace ThumpTool;

/// <summary>A hit: when, which drum ("kick", "snare", "hihat"), and how sure (1 for labels).</summary>
public readonly record struct Hit(double Time, string Drum, double Confidence = 1);

public sealed class DrumScore
{
    public int Labels, Detections, Matched;
    public readonly List<double> Offsets = [];
    public double Precision => Detections == 0 ? 0 : (double)Matched / Detections;
    public double Recall => Labels == 0 ? 0 : (double)Matched / Labels;
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
    /// <summary>Median of detection minus label time: how late the light is, on average.</summary>
    public double MedianLatency => Offsets.Count == 0 ? double.NaN : Offsets.Order().ElementAt(Offsets.Count / 2);

    public void Add(DrumScore o)
    {
        Labels += o.Labels;
        Detections += o.Detections;
        Matched += o.Matched;
        Offsets.AddRange(o.Offsets);
    }
}

public static class Scoring
{
    /// <summary>
    /// A detection counts for a label from 50 ms before it (labels are a little off too) to 80 ms after: the live
    /// detectors can only fire once a hit has started, and anything later than that no longer looks in time.
    /// </summary>
    public const double Before = 0.05, After = 0.08;

    /// <summary>Matches each label to the earliest unused detection of the same drum within the window.</summary>
    public static DrumScore Score(IEnumerable<Hit> labels, IEnumerable<Hit> detections, string drum)
    {
        var l = labels.Where(h => h.Drum == drum).Select(h => h.Time).Order().ToArray();
        var d = detections.Where(h => h.Drum == drum).Select(h => h.Time).Order().ToArray();
        var s = new DrumScore { Labels = l.Length, Detections = d.Length };
        int j = 0;
        foreach (var t in l)
        {
            while (j < d.Length && d[j] < t - Before) j++;
            if (j < d.Length && d[j] <= t + After)
            {
                s.Matched++;
                s.Offsets.Add(d[j] - t);
                j++;
            }
        }
        return s;
    }

    public static List<Hit> Read(string path) =>
        File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(l =>
        {
            var p = l.Split(',');
            return new Hit(double.Parse(p[0], CultureInfo.InvariantCulture), p[1].Trim(),
                p.Length > 2 ? double.Parse(p[2], CultureInfo.InvariantCulture) : 1);
        }).ToList();

    public static void Write(string path, IEnumerable<Hit> hits)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("time,drum,confidence");
        foreach (var h in hits)
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{h.Time:0.0000},{h.Drum},{h.Confidence:0.000}"));
    }
}
