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

    // Variations used when a source has fewer colours than there are lights: (hue shift °, saturation ×, value ×).
    // Order matters: with a plain orange cover and 4 lights you get orange, red-orange, deep orange, soft orange.
    static readonly (double H, double S, double V)[] Variations =
    [
        (-16, 1, 1),     // redder
        (0, 1, 0.55),    // deeper / darker
        (0, 0.5, 1),     // softer / lighter
        (14, 1, 1),      // ambery
        (-32, 1, 1),
        (28, 1, 1),
        (0, 0.25, 1),
        (-8, 1, 0.75),
    ];

    // For white/grey sources, where a hue shift means nothing: vary the colour temperature instead.
    static readonly Rgb[] WhiteVariations =
    [
        new(1, 0.82, 0.58), new(0.78, 0.86, 1), new(0.6, 0.6, 0.6), new(1, 0.7, 0.75), new(0.85, 0.75, 1), new(1, 0.93, 0.8),
    ];

    /// <summary>
    /// Returns at least <paramref name="count"/> clearly different colours, so every light gets its own colour.
    /// Near-duplicate source colours are merged first; missing colours are derived from the source colours.
    /// </summary>
    public static List<Rgb> Expand(IReadOnlyList<Rgb> colors, int count)
    {
        var result = new List<Rgb>();
        foreach (var c in colors)
            if (!result.Any(r => Distance(r, c) < 0.22)) result.Add(c); // e.g. two near-identical reds count as one
        if (result.Count == 0) result.Add(Rgb.White);

        int baseCount = result.Count;
        for (int v = 0; result.Count < count && v < 200; v++)
        {
            var src = result[v % baseCount];
            int round = v / baseCount;
            var (h, sat, val) = src.ToHsv();
            Rgb next;
            if (sat < 0.15)
            {
                next = WhiteVariations[round % WhiteVariations.Length];
            }
            else
            {
                var (dh, ds, dv) = Variations[round % Variations.Length];
                double spread = 1 + round / Variations.Length; // run out of variations → spread hues further
                next = Rgb.FromHsv(h + dh * spread, Math.Clamp(sat * ds, 0, 1), Math.Clamp(val * dv, 0, 1));
            }
            if (!result.Any(r => Distance(r, next) < 0.15)) result.Add(next);
        }
        return result;
    }

    static double Distance(Rgb a, Rgb b) =>
        Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));

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
