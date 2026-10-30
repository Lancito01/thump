using System.Globalization;

namespace Thump.Colors;

/// <summary>Linear-ish 0..1 RGB colour.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(1, 1, 1);

    public static Rgb operator *(Rgb c, double k) => new(c.R * k, c.G * k, c.B * k);

    public static Rgb Lerp(Rgb a, Rgb b, double t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    public double Max => Math.Max(R, Math.Max(G, B));

    /// <summary>Scales so the brightest channel is 1 (hue lights look washed out with dim source colours).</summary>
    public Rgb Normalized() => Max < 1e-4 ? Black : this * (1 / Max);

    public static Rgb FromHex(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6) throw new FormatException($"'{hex}' is not a 6-digit hex colour");
        int v = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
    }

    public string ToHex() =>
        $"#{(int)Math.Round(Math.Clamp(R, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(G, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(B, 0, 1) * 255):X2}";

    public (double H, double S, double V) ToHsv()
    {
        double max = Max, min = Math.Min(R, Math.Min(G, B)), d = max - min;
        double h = 0;
        if (d > 1e-6)
        {
            if (max == R) h = ((G - B) / d % 6 + 6) % 6;
            else if (max == G) h = (B - R) / d + 2;
            else h = (R - G) / d + 4;
            h *= 60;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    public static Rgb FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new(r + m, g + m, b + m);
    }

    /// <summary>Pushes saturation up by <paramref name="amount"/> (0 = unchanged, 1 = fully saturated).</summary>
    public Rgb Vivid(double amount)
    {
        var (h, s, v) = ToHsv();
        return FromHsv(h, s + (1 - s) * amount * s, v); // only boosts colours that already have some colour
    }
}
