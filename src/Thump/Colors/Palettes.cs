namespace Thump.Colors;

public static class Palettes
{
    public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>
    {
        ["Warehouse"] = ["#FF0000", "#FF2A00", "#B00020", "#FF0040"],
        ["Acid"] = ["#A6FF00", "#FF00C8", "#00FF6A", "#FFE600"],
        ["Berghain"] = ["#FF0000", "#FFFFFF", "#400000", "#FF3300"],
        ["Ice"] = ["#00E5FF", "#0055FF", "#9ADFFF", "#3A00FF"],
        ["Ultraviolet"] = ["#6A00FF", "#C800FF", "#2200FF", "#FF00AA"],
        ["Sunset"] = ["#FF4D00", "#FF0066", "#FFAA00", "#B0006A"],
        ["Rave"] = ["#FF0000", "#FFAA00", "#00FF00", "#00AAFF", "#AA00FF"],
        ["Strobe white"] = ["#FFFFFF", "#FFF4E0"],
    };

    public static List<Rgb> Parse(IEnumerable<string> hexes)
    {
        var list = new List<Rgb>();
        foreach (var h in hexes)
        {
            try { list.Add(Rgb.FromHex(h)); } catch (FormatException) { /* skip bad entries */ }
        }
        return list.Count > 0 ? list : [Rgb.White];
    }

    /// <summary>
    /// Picks the most prominent distinct, colourful hues from a BGRA image (e.g. album art).
    /// Weighted by saturation × brightness so a small neon logo can beat a big grey background.
    /// </summary>
    public static List<Rgb> Extract(ReadOnlySpan<byte> bgra, int maxColors = 5)
    {
        const int HueBins = 24;
        var weight = new double[HueBins];
        var sums = new (double R, double G, double B, double W)[HueBins];
        double grayW = 0;
        (double R, double G, double B) graySum = default;

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            var c = new Rgb(bgra[i + 2] / 255.0, bgra[i + 1] / 255.0, bgra[i] / 255.0);
            var (h, s, v) = c.ToHsv();
            if (v < 0.12) continue;
            if (s < 0.18)
            {
                grayW += v;
                graySum = (graySum.R + c.R * v, graySum.G + c.G * v, graySum.B + c.B * v);
                continue;
            }
            double w = s * s * v;
            int bin = (int)(h / 360 * HueBins) % HueBins;
            weight[bin] += w;
            sums[bin] = (sums[bin].R + c.R * w, sums[bin].G + c.G * w, sums[bin].B + c.B * w, sums[bin].W + w);
        }

        double total = weight.Sum();
        var result = new List<Rgb>();
        if (total > 0)
        {
            var taken = new bool[HueBins];
            for (int k = 0; k < maxColors; k++)
            {
                int best = -1;
                for (int b = 0; b < HueBins; b++)
                {
                    if (taken[b] || weight[b] < total * 0.03) continue;
                    // keep picked hues at least 2 bins (30°) apart
                    bool near = false;
                    for (int d = -1; d <= 1; d++) near |= taken[(b + d + HueBins) % HueBins];
                    if (near) continue;
                    if (best < 0 || weight[b] > weight[best]) best = b;
                }
                if (best < 0) break;
                taken[best] = true;
                var s = sums[best];
                result.Add(new Rgb(s.R / s.W, s.G / s.W, s.B / s.W).Normalized());
            }
        }

        if (result.Count == 0)
            result.Add(grayW > 0 ? new Rgb(graySum.R / grayW, graySum.G / grayW, graySum.B / grayW).Normalized() : Rgb.White);
        return result;
    }
}
